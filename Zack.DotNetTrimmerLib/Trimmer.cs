﻿using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Zack.DotNetTrimmerLib.PEHelpers;

namespace Zack.DotNetTrimmerLib;
public class Trimmer
{
    private TimmerOptions options;
    public event EventHandler<FileRemovedEventArgs> FileRemoved;
    public event EventHandler<MessageReceivedEventArgs> MessageReceived;

    public Trimmer(TimmerOptions options)
    {
        this.options = options;
    }

    private void FireFileRemoved(string fileFullPath)
    {
        if (FileRemoved != null)
        {
            FileRemoved(this, new FileRemovedEventArgs(fileFullPath));
        }
    }

    private void FireMessageReceived(string msg)
    {
        if (MessageReceived != null)
        {
            MessageReceived(this, new MessageReceivedEventArgs(msg));
        }
    }

    public bool Run()
    {
        string startupFile = options.StartupFile;
        string startupDir;

        var dir = Path.GetDirectoryName(startupFile);
        if (dir == null)
        {
            FireMessageReceived($"GetDirectoryName() of {startupFile} is empty");
            return false;
        }
        else
        {
            startupDir = dir;
        }

        HashSet<string> loadedAssemblies = new HashSet<string>();
        HashSet<string> loadedTypes = new HashSet<string>();
        var providers = new List<EventPipeProvider>()
        {
            new EventPipeProvider("Microsoft-Windows-DotNETRuntime",
                EventLevel.Informational, (long)ClrTraceEventParser.Keywords.All)
        };
        ProcessStartInfo psInfo = new ProcessStartInfo(startupFile);
        psInfo.UseShellExecute = true;
        psInfo.WorkingDirectory = startupDir;
        psInfo.Arguments = string.Join(" ", options.Arguments);
        Process? p = Process.Start(psInfo);
        if (p == null)
        {
            FireMessageReceived("Error: Starting process failed!");
            return false;
        }
        Console.CancelKeyPress += (_, ck) =>
        {
            //when the user pressed Ctrl+C, the trimmed process will be terminated first.
            if (p != null)
            {
                FireMessageReceived($"Application shutting down.");
                p.CloseMainWindow();
                p.WaitForExit();
                p = null;
                ck.Cancel = true;//prevent the current Trimmer process from being terminated by Ctrl+C
                FireMessageReceived($"Application shut down, waiting for to be trimmed.");
            }
        };

        var client = new DiagnosticsClient(p.Id);
        using EventPipeSession session = client.StartEventPipeSession(providers, false);
        var source = new EventPipeEventSource(session.EventStream);
        source.Clr.All += (TraceEvent obj) =>
        {
            if (obj is KnownPathProbedTraceData)
            {
                var data = (KnownPathProbedTraceData)obj;
                loadedAssemblies.Add(data.FilePath);
            }
            else if (obj is ResolutionAttemptedTraceData)
            {
                var data = (ResolutionAttemptedTraceData)obj;
                string asmFullPath = data.ResultAssemblyPath;
                if (!string.IsNullOrWhiteSpace(asmFullPath))
                {
                    loadedAssemblies.Add(asmFullPath);
                }
            }
            else if (obj is ModuleLoadUnloadTraceData)
            {
                var data = (ModuleLoadUnloadTraceData)obj;
                string asmFullPath = data.ModuleILPath;
                if (!string.IsNullOrWhiteSpace(asmFullPath))
                {
                    loadedAssemblies.Add(asmFullPath);
                }
            }
            //for Linux
            else if (obj is DomainModuleLoadUnloadTraceData)
            {
                var data = (DomainModuleLoadUnloadTraceData)obj;
                string asmFullPath = data.ModuleILPath;
                loadedAssemblies.Add(asmFullPath);
            }
            else if (obj is TypeLoadStopTraceData)
            {
                var data = (TypeLoadStopTraceData)obj;
                string typeName = data.TypeName;
                loadedTypes.Add(typeName);
            }
            else if (obj is GCBulkTypeTraceData)
            {
                var data = (GCBulkTypeTraceData)obj;
                for(int i=0;i<data.Count;i++)
                {
                    string typeName = data.Values(i).TypeName;
                    loadedTypes.Add(typeName);
                }
            }
            else if(obj is R2RGetEntryPointTraceData)
            {
                var data = (R2RGetEntryPointTraceData)obj;
                loadedTypes.Add(data.MethodNamespace);
            }
        };

        try
        {
            source.Process();
        }
        catch (Exception e)
        {
            FireMessageReceived($"Error encountered while processing events:{e}");
            return false;
        }


        var allDllFiles = Directory.GetFiles(startupDir, "*.dll", SearchOption.AllDirectories)
            .Where(asmPath => !IsFileIgnored(asmPath) && IsManagedAssembly(asmPath)).ToArray();
        var unloadedAssemblies = allDllFiles.Except(loadedAssemblies).ToArray();
        var totalSize = unloadedAssemblies.Select(f => new FileInfo(f).Length).Sum() * 1.0 / (1024 * 1024);
        foreach (string asmFile in unloadedAssemblies)
        {
            File.Delete(asmFile);
            FireFileRemoved(asmFile);
            string pdbFile = Path.ChangeExtension(asmFile, ".pdb");
            if (File.Exists(pdbFile))//delete related pdb files
            {
                File.Delete(pdbFile);
                FireFileRemoved(pdbFile);
            }
        }

        SlimAssemblies(loadedAssemblies, loadedTypes);

        foreach (var file in Directory.GetFiles(startupDir, "*.deps.json"))
        {
            TidyDeps_json(unloadedAssemblies, file);
            FireMessageReceived($"{file} tidied up.");
        }
        FireMessageReceived($"done, reduced file size:{totalSize:0.00} MB");
        FireMessageReceived("Waiting for exit.");
        if (p != null) p.Dispose();
        return true;
    }

    /// <summary>
    /// clear the bodies of methods of unused classes in loadedAssemblies
    /// </summary>
    /// <param name="loadedAssemblies"></param>
    /// <param name="loadedTypes"></param>
    private static void SlimAssemblies(HashSet<string> loadedAssemblies, HashSet<string> loadedTypes)
    {
        foreach (var asmFile in loadedAssemblies)
        {
            if (!File.Exists(asmFile)) continue;
            if (!IsManagedAssembly(asmFile)) continue;
            string fileName = Path.GetFileName(asmFile);
            if (fileName.Contains("Framework") || fileName.Contains("Core")) continue;
            using var memStream = new MemoryStream();
            using (var mod = ModuleDefMD.Load(asmFile))
            {

                List<TypeDef> typesToBeRemoved = new List<TypeDef>();
                foreach (var type in mod.Types)
                {
                    if (type.IsGlobalModuleType) continue;//<Module>

                    //struct, nested classes are not included in the report by DiagnosticsClient
                    if (type.IsValueType) continue;//UriCreationOptions etc
                    if (type.IsNested) continue;
                    if (type.HasGenericParameters) continue;//generic classes may have different names as in the report by DiagnosticsClient, like A`1 and A<int>
                    if (!loadedTypes.Contains(type.FullName))
                    {
                        typesToBeRemoved.Add(type);
                    }
                }
                foreach (var type in typesToBeRemoved)
                {
                    //these methods may be referenced by others, so they cannot be removed; however, the body can be replace by 'throw null;'
                    foreach (var method in type.Methods)
                    {
                        if (!method.IsManaged||method.IsConstructor || method.IsStaticConstructor) continue;
                        if (method.Body != null)
                        {
                            method.Body.ExceptionHandlers.Clear();
                            method.Body.Instructions.Clear();
                            method.Body.Variables.Clear();
                            method.Body.Instructions.Add(new Instruction(OpCodes.Nop) { Offset = 0 });
                            method.Body.Instructions.Add(new Instruction(OpCodes.Ldnull) { Offset = 1 });
                            method.Body.Instructions.Add(new Instruction(OpCodes.Throw) { Offset = 2 });
                        }
                    }
                }
                mod.Write(memStream);
            }
            using var fileStream = File.Open(asmFile, FileMode.Create);
            memStream.Position = 0;
            memStream.CopyTo(fileStream);
        }
    }

    static bool IsFileIgnored(string fileName)
    {
        var dirNames = Path.GetDirectoryName(fileName).Split(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return dirNames.Contains("_framework");//ignore files under _framework, for Blazor WebAssembly Projects.
    }

    static void FindNode(JsonObject jsonObject, string propertyName, List<JsonNode> runtimeNodes)
    {
        foreach (var childNode in jsonObject)
        {
            if (childNode.Key == propertyName&&childNode.Value!=null)
            {
                runtimeNodes.Add(childNode.Value);
            }
            else if (childNode.Value is JsonObject)
            {
                FindNode((JsonObject)childNode.Value, propertyName, runtimeNodes);
            }
        }
    }

    
    static void TidyDeps_json(IEnumerable<string> unloadedAssemblies,string jsonFile)
    {
        var unloadedFileNames = new HashSet<string>(unloadedAssemblies.Select(f => Path.GetFileName(f)));
        JsonNode jsonRoot;
        using (FileStream inStream = File.OpenRead(jsonFile))
        {
            jsonRoot = JsonNode.Parse(inStream).Root;            
        }
        var targetsNode = jsonRoot["targets"];
        if (targetsNode == null) return;
        List<JsonNode> runtimeNodes = new List<JsonNode>();
        FindNode((JsonObject)targetsNode, "runtime", runtimeNodes);
        foreach(var runtimeNode in runtimeNodes)
        {
            JsonObject runtimeObject = (JsonObject)runtimeNode;
            foreach (var runtimeItem in runtimeObject.ToArray())
            {
                string fileName = runtimeItem.Key;
                if (unloadedFileNames.Contains(fileName))
                {
                    runtimeObject.Remove(fileName);
                }
            }
        }
        JsonWriterOptions jsonWriterOptions = new JsonWriterOptions { Indented=true};
        using (FileStream outStream = File.Open(jsonFile,FileMode.Create,FileAccess.Write))
        using (Utf8JsonWriter writer = new Utf8JsonWriter(outStream, jsonWriterOptions))
        {
            jsonRoot.WriteTo(writer);
        }
    }
}