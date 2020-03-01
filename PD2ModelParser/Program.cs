﻿//Original code by PoueT

using PD2ModelParser.Misc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mono.Options;
// Currently the FBX exporter is the only class in the namespace, the others are in the PD2ModelParser namespace
#if !NO_FBX
using PD2ModelParser.Exporters;
using PD2ModelParser.Importers;
#endif
using PD2ModelParser.Sections;

namespace PD2ModelParser
{
    class Program
    {
        static IEnumerable<FileInfo> WalkDirectoryTreeDepth(DirectoryInfo dir, string filepattern)
        {
            foreach(var i in dir.EnumerateFiles(filepattern))
            {
                if (i.Attributes.HasFlag(FileAttributes.Directory)) continue;
                yield return i;
            }

            foreach(var i in dir.EnumerateDirectories())
            {
                foreach(var f in WalkDirectoryTreeDepth(i, filepattern))
                {
                    yield return f;
                }
            }
        }

        static void WriteSimpleCsvLike<T>(TextWriter tw, IEnumerable<T> items)
        {
            var fields = typeof(T).GetFields().OrderBy(i => i.MetadataToken).ToList();
            tw.WriteLine(string.Join(",",fields.Select(i=>i.Name)));
            var count = 1000;
            var inc = 0;
            foreach(var i in items)
            {
                var values = fields.Select(field => field.GetValue(i));
                tw.WriteLine(string.Join(",", values));
                if(count-- == 0)
                {
                    count = 1000;
                    tw.Flush();
                    Log.Default.Status($"{++inc}");
                }
            }
        }

        static IEnumerable<(string, FullModelData)> EveryModel(string root)
        {
            foreach(var i in WalkDirectoryTreeDepth(new DirectoryInfo(root), "*.model"))
            {
                var fmd = ModelReader.Open(i.FullName);
                yield return (i.FullName.Substring(root.Length), fmd);
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Log.Default = new ConsoleLogger();

            bool gui = HandleCommandLine(args);
            if (!gui)
                return;

            Updates.Startup();

            Form1 form = new Form1();
            Application.Run(form);
        }

        private static bool HandleCommandLine(string[] args)
        {
            // If there are no args, assume we're in GUI mode
            if (args.Length == 0)
            {
                // Set a sane default logging level to avoid console spam
                ConsoleLogger.minimumLevel = LoggerLevel.Info;

                return true;
            }

            bool show_help = false;
            bool gui = false;
            int verbosity = (int) LoggerLevel.Info;

            List<CommandLineEntry> actions = new List<CommandLineEntry>();

            OptionSet p = new OptionSet
            {
                {
                    "g|gui", "Enable the GUI",
                    v => gui = v != null
                },
                {
                    "v|verbose", "increase debug message verbosity",
                    v =>
                    {
                        if (v != null) --verbosity;
                    }
                },
                {
                    "q|quiet", "Decrease debug message verbosity",
                    v =>
                    {
                        if (v != null) ++verbosity;
                    }
                },
                {
                    "n|new-objects", "Sets whether or not new objects will be "
                                     + "created (append + or - to set this) default false",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.SetNewObj, v))
                },
                {
                    "r|root_point:", "Sets the default bone for new objects to be attached to",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.SetRootPoint, v))
                },
                {
                    "new", "Creates an entirely new model",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.New, v))
                },
                {
                    "load=", "Load a .model file",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.Load, v))
                },
                {
                    "save=", "Save to a .model file",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.Save, v))
                },
                {
                    "import=", "Imports from a 3D model file",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.Import, v))
                },
                {
                    "import-pattern-uv=", "Imports a pattern UV .OBJ file",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.ImportPatternUv, v))
                },
                {
                    "export=", "Exports to a .model file",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.Export, v))
                },
                {
                    "script=", "Executes a model script",
                    v => actions.Add(new CommandLineEntry(CommandLineActions.Script, v))
                },
                {
                    "h|help", "show this message and exit",
                    v => show_help = v != null
                },
            };

            List<string> extra;
            try
            {
                extra = p.Parse(args);
            }
            catch (OptionException e)
            {
                ShowError(e.Message);
                return false;
            }

            if (show_help)
            {
                ShowHelp(p);
                return false;
            }

            if (extra.Count != 0)
            {
                ShowError($"Unknown argument \"{extra[0]}\"");
                return false;
            }

            if (actions.Count != 0 && gui)
            {
                ShowError("Cannot process files in GUI mode!");
                return false;
            }

            if (verbosity > (int) LoggerLevel.Error || verbosity < 0)
            {
                Console.WriteLine("Cannot be that verbose or quiet (verbosity \n"
                                  + "{0} of 0-{1})!", verbosity, (int) LoggerLevel.Error);
                return false;
            }

            ConsoleLogger.minimumLevel = (LoggerLevel) verbosity;

            FullModelData data = null;

            bool new_obj = false;
            string root_point = null;
            int i = 0;
            foreach (CommandLineEntry entry in actions)
            {
                i++;

                // Check the model data exists unless we're loading or creating a new model
                if (entry.type != CommandLineActions.New
                    && entry.type != CommandLineActions.Load
                    && data == null)
                {
                    Console.WriteLine("Action {0}:{1} is run before a model is created or loaded!",
                        i, entry.type);
                    return false;
                }

                switch (entry.type)
                {
                    case CommandLineActions.SetNewObj:
                        new_obj = entry.arg != null;
                        Log.Default.Status("Setting new-objects to {0}", new_obj);
                        break;
                    case CommandLineActions.SetRootPoint:
                        root_point = entry.arg;
                        Log.Default.Status("Setting root-point to {0}", root_point);
                        break;
                    case CommandLineActions.New:
                        Log.Default.Status("Creating a new model");
                        data = new FullModelData();
                        break;
                    case CommandLineActions.Load:
                        Log.Default.Status("Loading .model {0}", entry.arg);
                        data = ModelReader.Open(entry.arg);
                        break;
                    case CommandLineActions.Save:
                        Log.Default.Status("Saving .model {0}", entry.arg);
                        DieselExporter.ExportFile(data, entry.arg);
                        break;
                    case CommandLineActions.Import:
                        Log.Default.Status("Importing file {0}", entry.arg);
                        if (entry.arg.EndsWith(".obj"))
                        {
                            uint rpid = FindRootPoint(data, root_point);
                            var root_object = data.parsed_sections[rpid] as Object3D;
                            NewObjImporter.ImportNewObj(
                                data,
                                entry.arg,
                                new_obj,
                                obj => root_object,
                                null
                                );
                        }
                        else if (entry.arg.EndsWith(".fbx"))
                        {
#if NO_FBX
                            Console.WriteLine("FBX support disabled");
                            return false;
#else
                            var options = new FilmboxImporter.FbxImportOptions();
                            FilmboxImporter.Import(data, entry.arg, new_obj, name => null, options);
#endif
                        }
                        else if (entry.arg.EndsWith(".dae"))
                        {
                            Console.WriteLine(
                                "Currently Collada imports are not yet supported (for {0})",
                                entry.arg);
                            return false;
                        }
                        else if (entry.arg.EndsWith(".gltf") || entry.arg.EndsWith(".glb"))
                        {
                            Importers.GltfImporter.Import(data, entry.arg, new_obj, obj => null, null);
                        }
                        else
                        {
                            Console.WriteLine(
                                "Unknown or unsupported file extension for import for {0}",
                                entry.arg);
                            return false;
                        }

                        break;
                    case CommandLineActions.ImportPatternUv:
                        Log.Default.Status("Importing pattern UV {0}", entry.arg);
                        if (entry.arg.EndsWith(".obj"))
                        {
                            bool result = NewObjImporter.ImportNewObjPatternUV(
                                data,
                                entry.arg);
                            if (!result)
                            {
                                return false;
                            }
                        }
                        else
                        {
                            Console.WriteLine(
                                "Only OBJ needs pattern imports (for {0})",
                                entry.arg);
                            return false;
                        }

                        break;
                    case CommandLineActions.Export:
                        Log.Default.Status("Exporting to {0}", entry.arg);
                        if (entry.arg.EndsWith(".dae"))
                        {
                            ColladaExporter.ExportFile(data, entry.arg);
                        }
                        else if (entry.arg.EndsWith(".fbx"))
                        {
#if NO_FBX
                            Console.WriteLine("FBX support disabled");
                            return false;
#else
                            FbxExporter.ExportFile(data, entry.arg);
#endif
                        }
                        else if (entry.arg.EndsWith(".obj"))
                        {
                            ObjWriter.ExportFile(data, entry.arg);
                        }
                        else if (entry.arg.EndsWith(".gltf") || entry.arg.EndsWith(".glb"))
                        {
                            Exporters.GltfExporter.ExportFile(data, entry.arg);
                        }
                        else
                        {
                            Console.WriteLine(
                                "Unknown or unsupported file extension for export for {0}",
                                entry.arg);
                            return false;
                        }

                        break;
                    case CommandLineActions.Script:
                        Log.Default.Status("Executing script {0}", entry.arg);
                        try
                        {
                            ModelScript.Execute(data, entry.arg);
                        }
                        catch (Exception ex)
                        {
                            Log.Default.Warn("Stacktrace: {0}", ex.StackTrace);
                            Log.Default.Error("Error executing model script {0}: {1}",
                                entry.arg, ex.Message);
                        }

                        break;
                    default:
                        Log.Default.Error("Unknown action {0}", entry.type);
                        break;
                }
            }

            return gui;
        }

        private static void ShowError(string err)
        {
            string exe_name = AppDomain.CurrentDomain.FriendlyName;
            Console.Write("{0}:", exe_name);
            Console.WriteLine(err);
            Console.WriteLine("Try `{0} --help' for more information.", exe_name);
        }

        private static uint FindRootPoint(FullModelData data, string root_point)
        {
            if (root_point == null)
                return 0;

            var point = data.SectionsOfType<Object3D>()
                .Where(o => o.Name == root_point)
                .FirstOrDefault();
            return point?.SectionId ?? throw new Exception($"Root point {root_point} not found!");
        }

        private static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: {0} [OPTIONS]+", AppDomain.CurrentDomain.FriendlyName);
            Console.WriteLine("Import and export DieselX .model files");
            Console.WriteLine("If the program is called without any arguments,");
            Console.WriteLine(" GUI mode is enabled by default");
            Console.WriteLine("If the program is running in command-line mode, then");
            Console.WriteLine(" the file commands will be run in sequence");
            Console.WriteLine(" eg, --load=a.model --import=b.obj --root-point=Hips "
                              + "--import-pattern-uv=b_pattern_uv.obj --save=c.model");
            Console.WriteLine(" Will import a model called a.model, add an object, and save it as c.model");
            Console.WriteLine(" Note you can process many models in one run of the program, which is faster");
            Console.WriteLine(" than running the program once for each model");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }

        private enum CommandLineActions
        {
            SetNewObj,
            SetRootPoint,
            New,
            Load,
            Save,
            Import,
            ImportPatternUv,
            Export,
            Script,
        }

        private struct CommandLineEntry
        {
            internal readonly CommandLineActions type;
            internal readonly string arg;

            public CommandLineEntry(CommandLineActions type, string arg)
            {
                this.type = type;
                this.arg = arg;
            }
        }
    }

    internal class ConsoleLogger : BaseLogger
    {
        internal static LoggerLevel minimumLevel;

        public override void Log(LoggerLevel level, string message, params object[] value)
        {
            if (level < minimumLevel)
                return;

            Console.WriteLine(level + @": " + GetCallerName(3) + @": " + string.Format(message, value));
        }
    }
}
