using System;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;

using static ResoniteBridge.ReflectionUtils;
using ResoniteUnityExporterShared;
using System.Diagnostics;

namespace ResoniteBridge
{
    public class FrooxEngineRunner
    {
        // Originally modified from https://github.com/Lexevolution/Resonite-DataTree-Converter/blob/main/Program.cs
        // WinForms OpenFileDialog removed (it was the only thing forcing a net8.0-windows/Windows-only build) -
        // path is now sourced, in priority order: CLI arg -> RESONITE_EXE_PATH env var -> saved config file ->
        // interactive console prompt. This also lets the Unity Editor package drive it headlessly (native Unity
        // file picker via EditorUtility.OpenFilePanel, passed in as a CLI arg or the env var) instead of this
        // process needing its own GUI at all.
        public static string GetResoniteExePath(out Dictionary<string, Assembly> libraries, string[] args)
        {
            string settingsLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResoniteUnityExporterStandalone", "app.config");
            bool success = false;
            libraries = new Dictionary<string, Assembly>();
            string resoniteExeLocation = "";

            string preseededPath = null;
            if (args != null && args.Length > 0 && File.Exists(args[0]))
            {
                preseededPath = args[0];
            }
            else if (File.Exists(Environment.GetEnvironmentVariable("RESONITE_EXE_PATH")))
            {
                preseededPath = Environment.GetEnvironmentVariable("RESONITE_EXE_PATH");
            }
            if (preseededPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsLocation));
                File.WriteAllText(settingsLocation, preseededPath);
                Console.WriteLine("Using Resonite.exe location from " + (args != null && args.Length > 0 ? "command line argument" : "RESONITE_EXE_PATH") + ": " + preseededPath);
            }

            while (!success)
            {
                while (!File.Exists(settingsLocation))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsLocation));
                    Console.WriteLine("Please enter the full path to Resonite.exe (or Resonite, on Linux/Mac), then press Enter:");
                    string typedPath = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(typedPath))
                    {
                        Console.WriteLine("No path entered, try again.");
                        Console.WriteLine("");
                        continue;
                    }
                    typedPath = typedPath.Trim().Trim('"');
                    if (!File.Exists(typedPath))
                    {
                        Console.WriteLine("That path doesn't exist: " + typedPath);
                        Console.WriteLine("");
                        continue;
                    }
                    File.WriteAllText(settingsLocation, typedPath);
                    Console.WriteLine("Wrote resonite.exe location " + typedPath + " to " + settingsLocation);
                }
                StreamReader sr = new StreamReader(settingsLocation);
                resoniteExeLocation = sr.ReadToEnd();
                sr.Close();
                if (!File.Exists(resoniteExeLocation))
                {
                    File.Delete(settingsLocation);
                    continue; // retry
                }
                Console.WriteLine(string.Format("DIRECTORY: {0}", Path.GetDirectoryName(resoniteExeLocation)));
                libraries.Clear();
                string resoniteFolder = Path.GetDirectoryName(resoniteExeLocation);
                // Post-"Splittening" installs no longer have a Resonite_Data\Managed subfolder - managed
                // (and native) DLLs now sit directly in the install root.
                string libraryFolder = resoniteFolder;

                // needed so it can fetch various config stuff
                Directory.SetCurrentDirectory(resoniteFolder);

                Environment.SetEnvironmentVariable("PATH",
                    Environment.GetEnvironmentVariable("PATH") + ";"
                    + libraryFolder + ";"
                    + resoniteFolder); // for assimp.dll


                AppDomain.CurrentDomain.AssemblyResolve += delegate (object sender, ResolveEventArgs args)
                {
                    string assemblyFile = (args.Name.Contains(','))
                        ? args.Name.Substring(0, args.Name.IndexOf(','))
                        : args.Name;

                    assemblyFile += ".dll";


                    string targetPath = Path.Combine(libraryFolder, assemblyFile);

                    try
                    {
                        return Assembly.LoadFile(targetPath);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                };


                Console.WriteLine("current path" + Environment.GetEnvironmentVariable("PATH"));
                try
                {
                    libraries.Add("FrooxEngine", Assembly.Load("FrooxEngine"));
                    libraries.Add("FrooxEngine.Store", Assembly.Load("FrooxEngine.Store"));
                    libraries.Add("ProtoFlux.Core", Assembly.Load("ProtoFlux.Core"));
                    libraries.Add("ProtoFlux.Nodes.Core", Assembly.Load("ProtoFlux.Nodes.Core"));
                    libraries.Add("ProtoFlux.Nodes.FrooxEngine", Assembly.Load("ProtoFlux.Nodes.FrooxEngine"));
                    libraries.Add("ProtoFluxBindings", Assembly.Load("ProtoFluxBindings"));
                    libraries.Add("SkyFrost.Base", Assembly.Load("SkyFrost.Base"));
                    libraries.Add("SkyFrost.Base.Models", Assembly.Load("SkyFrost.Base.Models"));
                    libraries.Add("Elements.Core", Assembly.Load("Elements.Core"));
                    success = true;
                }
                catch
                {
                    Console.WriteLine("Resonite.exe not detected. Restarting...");
                    Console.WriteLine("");
                    File.Delete(settingsLocation);
                }
            }
            return resoniteExeLocation;
        }


        public object engine;
        public object systemInfo;
        public object focusedWorld;
        public Assembly FrooxEngineAsm;
        public Assembly SkyFrostBaseModelsAsm;
        public object mainRootSlot;

        // heavily modified from code given to me by whatsavalue3 (who gave permission to license this as MIT)
        public FrooxEngineRunner(string[] args = null)
        {

            string resoniteDir = Path.GetDirectoryName(
                FrooxEngineRunner.GetResoniteExePath(out assemblies, args)
            );
            string curDir = System.IO.Directory.GetCurrentDirectory();
            // Post-"Splittening": no more Resonite_Data\Managed subfolder, DLLs sit in the install root.
            string libraryPath = resoniteDir;


            assemblies.TryGetValue("FrooxEngine", out FrooxEngineAsm);
            assemblies.TryGetValue("SkyFrost.Base.Models", out SkyFrostBaseModelsAsm);

            // Once we have froox engine, load all assemblies (this will collect more as they are loaded)
            assemblies = ReflectionUtils.LoadAssemblies(FrooxEngineAsm,
                   resoniteDir,
                   resoniteDir
                   );


            object launchOptions = CallConstructor(FrooxEngineAsm, "FrooxEngine.LaunchOptions");

            // Post-"Splittening": Resonite_Data\Data is now just RuntimeData in the install root.
            SetProperty(launchOptions, "DataDirectory",
                Path.Combine(resoniteDir, "RuntimeData"));
            // Cache needs to be local in order to run this while Resonite is also running
            SetProperty(launchOptions, "CacheDirectory",
                Path.Combine(curDir, "Cache"));
            SetProperty(launchOptions, "LogsDirectory",
                Path.Combine(resoniteDir, "Logs"));
            SetProperty(launchOptions, "CloudProfile",
                GetEnum(FrooxEngineAsm, "FrooxEngine.CloudProfile", "Production"));
            SetProperty(launchOptions, "VerboseInit",
                false);
            // see InitializeFrooxEngine where it tries to manually load them
            // this prevents it from finding dlls and loading them twice, since we manually loaded them above
            var tmpDir = Directory.CreateDirectory("Stap looking up");
            Directory.SetCurrentDirectory(tmpDir.FullName);

            engine = CallConstructor(FrooxEngineAsm, "FrooxEngine.Engine");
            systemInfo = CallConstructor(FrooxEngineAsm, "FrooxEngine.StandaloneSystemInfo");

            System.Threading.Tasks.Task task = (System.Threading.Tasks.Task)
                CallMethod(engine, "Initialize",
                    libraryPath,
                    launchOptions,
                    systemInfo,
                    null,
                    CallConstructor(FrooxEngineAsm, "FrooxEngine.ConsoleEngineInitProgress"));
            var configuredTaskAwaiter = task.ConfigureAwait(false).GetAwaiter();

            while (!configuredTaskAwaiter.IsCompleted)
            {
                Thread.Sleep(16);
            }
            // remove tmp dir
            Directory.SetCurrentDirectory(resoniteDir);
            Directory.Delete(tmpDir.FullName);

            // if you want to login, optional
            //var consolelogin = ((FrooxEngine.Engine)this.engine).Cloud.InteractiveConsoleLogin().ConfigureAwait(false).GetAwaiter();
            //((FrooxEngine.Engine)this.engine).Cloud.
            //while (!consolelogin.IsCompleted)
            //{

            //}
            var userspaceWorld = CallMethod(LookupType("FrooxEngine", "FrooxEngine.Userspace"), "SetupUserspace", engine);
            CallMethod(engine, "RunUpdateLoop");
            
            object worldStart = CallConstructor(FrooxEngineAsm, "FrooxEngine.WorldStartSettings");
            SetField(worldStart, "AutoFocus", true);
            SetField(worldStart, "DefaultAccessLevel", 
                GetEnum(SkyFrostBaseModelsAsm, "SkyFrost.Base.SessionAccessLevel", "Private"));

            Delegate initWorldDelegate = 
                CreateDelegateWithInputType(FrooxEngineAsm,
                "FrooxEngine.World",
                "FrooxEngine.WorldAction",
                delegate (object world)
                {
                    CallMethod(LookupType("FrooxEngine", "FrooxEngine.WorldPresets"), "Grid", world);
                    SetProperty(world, "AccessLevel",
                        GetEnum(SkyFrostBaseModelsAsm, "SkyFrost.Base.SessionAccessLevel", "Private"));
                    SetField(world, "ForceAnnounceOnWAN", false);
                    SetProperty(world, "MaxUsers", 1);
                    SetProperty(world, "Name", "ResoniteDataWrapper");
                    mainRootSlot = CallMethod(world, "AddSlot", "ResoniteDataWrapperSlotRoot");
                });
            SetField(worldStart, "InitWorld", initWorldDelegate);

            System.Threading.Tasks.Task opener = (System.Threading.Tasks.Task)
                CallMethod(
                    LookupType("FrooxEngine", "FrooxEngine.Userspace"),
                    "OpenWorld",
                    worldStart);
            opener.ConfigureAwait(false).GetAwaiter();

            ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
            new Thread(() =>
            {
                ManualResetEvent readyToProcess = new ManualResetEvent(false);

                ResoniteBridgeLib.ResoniteBridgeServer bridgeServer = null;

                bool first = true;
                try
                {
                    Console.WriteLine("Starting");
                    while (!opener.IsCompleted)
                    {
                        CallMethod(engine, "RunUpdateLoop");
                        CallMethod(systemInfo, "FrameFinished");
                        Thread.Sleep(16);
                    }
                    Console.WriteLine("Started");
                    var cancellation = new CancellationTokenSource();
                    while (true)
                    {
                        object focusedWorld =
                            GetProperty(
                                GetProperty(engine, "WorldManager"),
                                "FocusedWorld");

                        if (focusedWorld != null)
                        {
                            if (first)
                            {

                                string serverDirectory =
                                    Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "UnityResoniteImporter",
                                        "IPCConnections",
                                        "Servers"
                                    );
                                bridgeServer = new ResoniteBridgeLib.ResoniteBridgeServer("UnityResoniteImporter", serverDirectory, (msg) =>
                                {
                                    // observing thread occupancy (if needed)
                                    //int workerThreads, completionPortThreads;
                                    //int maxWorkerThreads, maxCompletionPortThreads;

                                    // Get available threads
                                    //ThreadPool.GetAvailableThreads(out workerThreads, out completionPortThreads);

                                    // Get maximum threads
                                    //ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxCompletionPortThreads);
                                    //Console.WriteLine("threads: " + workerThreads + " " + completionPortThreads + " " + maxWorkerThreads + " " + maxCompletionPortThreads);

                                    //Console.WriteLine(msg);
                                });
                                ImportFromUnityLib.ImportFromUnityLib.Register(bridgeServer, () =>
                                {
                                    return new ServerInfo_U2Res()
                                    {
                                        allowedToCreateInWorld = true,
                                        worldName = "World",
                                        label = "Standalone",
                                    };
                                },
                                msg => Console.WriteLine(msg), CurrentEngine: engine);
                                first = false;

                                // hack to prevent discord interface from crashing it
                                var platformConnectorType = LookupType("FrooxEngine", "FrooxEngine.IPlatformConnector");
                                // todo: less aggressive version of this
                                SetField(
                                    GetProperty(engine, "PlatformInterface"),
                                    "connectors",
                                    Array.CreateInstance(platformConnectorType
                                        ,
                                        0)
                                    );
                                
                                readyToProcess.Set();
                            }   
                        }
                        try
                        {
                            CallMethod(engine, "RunUpdateLoop");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Got exception " + e.ToString() + "\n" + Environment.StackTrace);
                        }
                        CallMethod(systemInfo, "FrameFinished");
                        Thread.Sleep(16);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString() + "\n" + Environment.StackTrace);
                }
            }).Start();
        }
    }

    internal class Program
    {
        static void Main(string[] args)
        {
            FrooxEngineRunner runner = new FrooxEngineRunner(args);
        }
    }
}
