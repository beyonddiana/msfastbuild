// msfastbuild.cs - Generates and executes a bff file for fastbuild from a .sln or .vcxproj.
// Copyright 2016 Liam Flookes and Yassine Riahi
// Available under an MIT license. See license file on github for details.
using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using CommandLine.Text;
using System.IO;
using System.Reflection;
using System.Text;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities;
using System.Text.RegularExpressions;

namespace msfastbuild
{
	public class Options
	{
		[Option('p', "vcproject", DefaultValue = "",
		HelpText = "Path of vcproj to be built. Or name of vcproj if a solution is provided.")]
		public string Project { get; set; }

		[Option('s', "sln", DefaultValue = "",
		HelpText = "sln which contains the vcproj.")]
		public string Solution { get; set; }

		[Option('c', "config", DefaultValue = "Debug",
		HelpText = "Configuration to build.")]
		public string Config { get; set; }

		[Option('f', "platform", DefaultValue = "Win32",
		HelpText = "Platform to build.")]
		public string Platform { get; set; }

		[Option('a', "fbargs", DefaultValue = "-dist",
		HelpText = "Arguments to pass through to FASTBuild.")]
		public string FBArgs { get; set; }

		[Option('g', "generateonly", DefaultValue = false,
		HelpText = "Generate the bff file without calling FASTBuild.")]
		public bool GenerateOnly { get; set; }

		[Option('r', "regen", DefaultValue = false, //true for dev
		HelpText = "If true, regenerate the bff file even when the project hasn't changed.")]
		public bool AlwaysRegenerate { get; set; }

		//@"E:\fastbuild\tmp\x64-Release\Tools\FBuild\FBuildApp\FBuild.exe"
		[Option('b', "fbpath", DefaultValue = @"FBuild.exe",
		HelpText = "Path to FASTBuild executable.")]
		public string FBPath { get; set; }

		[Option('u', "unity", DefaultValue = false,
		HelpText = "Whether to combine files into a unity step. May substantially improve compilation time, but not all projects are suitable.")]
		public bool UseUnity { get; set; }

		[Option('t', "relativepaths", DefaultValue = false,
		HelpText = "If true, add .UseRelativePaths_Experimental to Compiler('msvc')")]
		public bool UseRelativePaths { get; set; }

		[Option('l', "lightcache", DefaultValue = false,
		HelpText = "If true, add .UseLightCache_Experimental to Compiler('msvc')")]
		public bool UseLightCache { get; set; }

		[HelpOption]
		public string GetUsage()
		{
			return HelpText.AutoBuild(this,(HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}

	public class msfastbuild
	{
		static public string PlatformToolsetVersion = "140";
		static public string VCBasePath = "";
		static public string BFFOutputFilePath = "fbuild.bff";
		static public Options CommandLineOptions = new Options();
		static public string WindowsSDKTarget = "10.0.10240.0";
		static public MSFBProject CurrentProject;
		static public Assembly CPPTasksAssembly;
		static public string PreBuildBatchFile = "";
		static public string PostBuildBatchFile = "";
		static public string SolutionDir = "";
		static public bool HasCompileActions = true;

		public enum BuildType
		{
		    Application,
		    StaticLib,
		    DynamicLib
		}

        static public double TotalTime = 0;
		static public BuildType BuildOutput = BuildType.Application;

		public class MSFBProject
		{
			public Project Proj;
			public List<MSFBProject> Dependents = new List<MSFBProject>();
			public string AdditionalLinkInputs = "";
		}

		static int Main(string[] args)
		{
			Parser parser = new Parser();
			if (!parser.ParseArguments(args, CommandLineOptions))
			{
				Console.WriteLine(CommandLineOptions.GetUsage());
				return 1;
			}

			if (string.IsNullOrEmpty(CommandLineOptions.Solution) && string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				Console.WriteLine("No vcxproj or sln provided: nothing to do!");
				Console.WriteLine(CommandLineOptions.GetUsage());
                return 1;
            }

			List <string> ProjectsToBuild = new List<string>();
			if (!string.IsNullOrEmpty(CommandLineOptions.Solution) && File.Exists(CommandLineOptions.Solution))
			{
				try
				{
					if (string.IsNullOrEmpty(CommandLineOptions.Project))
					{
						List<ProjectInSolution> SolutionProjects = SolutionFile.Parse(Path.GetFullPath(CommandLineOptions.Solution)).ProjectsInOrder.Where(el => el.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat).ToList();
						SolutionProjects.Sort((x, y) => //Very dubious sort.
						{
							if (x.Dependencies.Contains(y.ProjectGuid)) return 1;
							if (y.Dependencies.Contains(x.ProjectGuid)) return -1;
							return 0;
						});
						ProjectsToBuild = SolutionProjects.ConvertAll(el => el.AbsolutePath);
					}
					else
					{
						ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
					}

					SolutionDir = Path.GetDirectoryName(Path.GetFullPath(CommandLineOptions.Solution));
					SolutionDir = SolutionDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					if(SolutionDir.Last() != Path.AltDirectorySeparatorChar)
						SolutionDir += Path.AltDirectorySeparatorChar;
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to load input file: " + CommandLineOptions.Solution + ", exception thrown was: " + e.Message);
                    return 1;
                }
			}
			else if (!string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
			}

			List<MSFBProject> EvaluatedProjects = new List<MSFBProject>();

			for (int i=0; i < ProjectsToBuild.Count; ++i)
			{
				EvaluateProjectReferences(ProjectsToBuild[i], EvaluatedProjects, null);
			}

			List<List<string>> build_list = new List<List<string>>();
			System.Diagnostics.Stopwatch stop_watch = new System.Diagnostics.Stopwatch();
			stop_watch.Start();
			int ProjectsBuilt = 0;
			foreach(MSFBProject project in EvaluatedProjects)
			{
				CurrentProject = project;
                //MSBuild 15 (2017?) may not provide these properties.
                string VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPath16");
                if(string.IsNullOrEmpty(VCTargetsPath))
                {
                    VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPath");
                }
                if(string.IsNullOrEmpty(VCTargetsPath))
                {
                    VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPathActual");
                }
                if(string.IsNullOrEmpty(VCTargetsPath))
                {
                    Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + Path.GetFileName(CurrentProject.Proj.FullPath) + ". Is this a supported version of Visual Studio?");
                    continue;
                }
                string BuildDllPath = VCTargetsPath + "Microsoft.Build.CPPTasks.Common.dll";
                if(!File.Exists(BuildDllPath))
                {
                    Console.WriteLine("Failed to find " + BuildDllPath + ". Is this a supported version of Visual Studio?");
                    continue;
                }

                CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);
				BFFOutputFilePath = Path.GetDirectoryName(CurrentProject.Proj.FullPath) + "\\" + Path.GetFileName(CurrentProject.Proj.FullPath) + "_" + CommandLineOptions.Config.Replace(" ", "") + "_" + CommandLineOptions.Platform.Replace(" ", "") + ".bff";
				GenerateBffFromVcxproj(CommandLineOptions.Config, CommandLineOptions.Platform);

				if (!CommandLineOptions.GenerateOnly)
				{
					if (HasCompileActions)
					{
						if (BuildOutput != BuildType.Application)
						{
							string FBArgs = Regex.Replace(CommandLineOptions.FBArgs, @"-j[0-9]*", "");
							if (HasCompileActions && !ExecuteBffFile(CurrentProject.Proj.FullPath, CommandLineOptions.Platform, BFFOutputFilePath, FBArgs))
							{
								break;
							}
							else
							{
								ProjectsBuilt++;
							}
						}
						else
						{
							var l = new List<string>();
							l.Add(CurrentProject.Proj.FullPath);
							l.Add(CommandLineOptions.Platform);
							l.Add(BFFOutputFilePath);

							build_list.Add(l);
						}
					}
				}
			}

			List<System.Threading.Tasks.Task<bool>> list_threads = new List<System.Threading.Tasks.Task<bool>>();
			foreach (var b_args in build_list)
			{
				list_threads.Add(
					System.Threading.Tasks.Task.Factory.StartNew(() =>
					{
						return ExecuteBffFile(b_args[0], b_args[1], b_args[2], CommandLineOptions.FBArgs);
					})
				);
			}

			foreach (var t in list_threads)
			{
				t.Wait();
				if (t.Result)
				{
					++ProjectsBuilt;
				}
			}
			stop_watch.Stop();
			TotalTime = stop_watch.Elapsed.TotalSeconds;

			UInt32 minutes = (UInt32)(TotalTime / 60.0f);
            TotalTime -= (minutes * 60.0f);
            double seconds = TotalTime;
			Console.WriteLine("");
			if (minutes > 0)
            {
                Console.WriteLine("Total Time: " + minutes.ToString() + "m " + seconds.ToString("0.###") + "s");
            }
            else
            {
                Console.WriteLine("Total Time: " + seconds.ToString("0.###") + "s");
            }

			Console.WriteLine($"========== 빌드: 성공 {ProjectsBuilt}, 실패 {EvaluatedProjects.Count - ProjectsBuilt} ==========");


			if (ProjectsBuilt != EvaluatedProjects.Count)
            {
                return 1;
            }

            return 0;
        }

		static public void EvaluateProjectReferences(string ProjectPath, List<MSFBProject> evaluatedProjects, MSFBProject dependent)
		{
			if (!string.IsNullOrEmpty(ProjectPath) && File.Exists(ProjectPath))
			{
				try
				{
					MSFBProject newProj = evaluatedProjects.Find(elem => elem.Proj.FullPath == Path.GetFullPath(ProjectPath));
					if (newProj != null)
					{
						//Console.WriteLine("Found exisiting project " + Path.GetFileNameWithoutExtension(ProjectPath));
						if (dependent != null)
							newProj.Dependents.Add(dependent);
					}
					else
					{
						ProjectCollection projColl = new ProjectCollection();
						if (!string.IsNullOrEmpty(SolutionDir))
							projColl.SetGlobalProperty("SolutionDir", SolutionDir);
						newProj = new MSFBProject();
						Project proj = projColl.LoadProject(ProjectPath);

						if (proj != null)
						{
							proj.SetGlobalProperty("Configuration", CommandLineOptions.Config);
							proj.SetGlobalProperty("Platform", CommandLineOptions.Platform);
							if (!string.IsNullOrEmpty(SolutionDir))
								proj.SetGlobalProperty("SolutionDir", SolutionDir);
							proj.ReevaluateIfNecessary();

							newProj.Proj = proj;
							if (dependent != null)
							{
								newProj.Dependents.Add(dependent);
							}
							var ProjectReferences = proj.Items.Where(elem => elem.ItemType == "ProjectReference");
							foreach (var ProjRef in ProjectReferences)
							{
								if (ProjRef.GetMetadataValue("ReferenceOutputAssembly") == "true" || ProjRef.GetMetadataValue("LinkLibraryDependencies") == "true")
								{
									//Console.WriteLine(string.Format("{0} referenced by {1}.", Path.GetFileNameWithoutExtension(ProjRef.EvaluatedInclude), Path.GetFileNameWithoutExtension(proj.FullPath)));
									EvaluateProjectReferences(Path.GetDirectoryName(proj.FullPath) + Path.DirectorySeparatorChar + ProjRef.EvaluatedInclude, evaluatedProjects, newProj);
								}
							}
							//Console.WriteLine("Adding " + Path.GetFileNameWithoutExtension(proj.FullPath));
							evaluatedProjects.Add(newProj);
						}
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Exception loading project " + ProjectPath + " exception " + e.Message);
					return;
				}
			}
		}

		static public bool HasFileChanged(string InputFile, string Platform, string Config, out string MD5hash)
		{
			using (var md5 = System.Security.Cryptography.MD5.Create())
			{
				using (var stream = File.OpenRead(InputFile))
				{
				    MD5hash = ";" + InputFile + "_" + Platform + "_" + Config + "_" + BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
				}
			}

			if (!File.Exists(BFFOutputFilePath))
				return true;

			string FirstLine = File.ReadAllLines(BFFOutputFilePath).First(); //bit wasteful to read the whole file...
			if (FirstLine == MD5hash)
				return false;
			else
				return true;
		}

		static public bool ExecuteBffFile(string ProjectPath, string Platform, string BFFPath, string FBArgs)
		{
			string projectDir = Path.GetDirectoryName(ProjectPath) + "\\";

			string BatchFileText = "@echo off\n"
				+ "%comspec% /c \"\"" + VCBasePath
				+ "Auxiliary\\Build\\vcvarsall.bat\" " + (Platform == "Win32" ? "x86" : "x64") + " "
				+ WindowsSDKTarget
				+ " && \"" + CommandLineOptions.FBPath  +"\" %*\"";
			File.WriteAllText(projectDir + "fb.bat", BatchFileText);

			Console.WriteLine("Building " + Path.GetFileNameWithoutExtension(ProjectPath));

			try
			{
				System.Diagnostics.Process FBProcess = new System.Diagnostics.Process();
				FBProcess.StartInfo.FileName = projectDir + "fb.bat";
				FBProcess.StartInfo.Arguments = "-config \"" + BFFPath + "\" " + FBArgs;
				FBProcess.StartInfo.RedirectStandardOutput = true;
				FBProcess.StartInfo.UseShellExecute = false;
				FBProcess.StartInfo.WorkingDirectory = projectDir;
				FBProcess.StartInfo.StandardOutputEncoding = Console.OutputEncoding;

				FBProcess.Start();
                string readLine = "";
				while (!FBProcess.StandardOutput.EndOfStream)
				{
                    readLine = FBProcess.StandardOutput.ReadLine();
                    Console.Write(readLine + "\n");
				}

                FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Problem launching fb.bat. Exception: " + e.Message);
				return false;
			}
		}

		public class ObjectListNode
		{
			string Compiler;
			string CompilerOutputPath;
			string CompilerOptions;
			string CompilerOutputExtension;
			string PrecompiledHeaderString;

			List<string> CompilerInputFiles;

			public ObjectListNode(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions, string InPrecompiledHeaderString, string InCompilerOutputExtension = "")
			{
				CompilerInputFiles = new List<string>();
				CompilerInputFiles.Add(InputFile);
				Compiler = InCompiler;
				CompilerOutputPath = InCompilerOutputPath;
				CompilerOptions = InCompilerOptions;
				CompilerOutputExtension = InCompilerOutputExtension;
				PrecompiledHeaderString = InPrecompiledHeaderString;
			}

			public bool AddIfMatches(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions, string InPrecompiledHeaderString)
			{
				if(Compiler == InCompiler && CompilerOutputPath == InCompilerOutputPath && CompilerOptions == InCompilerOptions && PrecompiledHeaderString == InPrecompiledHeaderString)
				{
					CompilerInputFiles.Add(InputFile);
					return true;
				}
				return false;
			}

			public string ToString(int ActionNumber)
			{
				bool UsedUnity = false;
				string ResultString = "";
				if(CommandLineOptions.UseUnity && Compiler != "rc" && CompilerInputFiles.Count > 1)
				{
					StringBuilder UnityListString = new StringBuilder(string.Format("Unity('unity_{0}')\n{{\n", ActionNumber));
					UnityListString.AppendFormat("\t.UnityInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", el)).ToArray()));
					UnityListString.AppendFormat("\t.UnityOutputPath = \"{0}\"\n", CompilerOutputPath);
					UnityListString.AppendFormat("\t.UnityNumFiles = {0}\n", 1 + CompilerInputFiles.Count/10);
					UnityListString.Append("}\n\n");
					UsedUnity = true;
					ResultString = UnityListString.ToString();
				}

				StringBuilder ObjectListString = new StringBuilder(string.Format("ObjectList('action_{0}')\n{{\n", ActionNumber));
				ObjectListString.AppendFormat("\t.Compiler = '{0}'\n", Compiler);
				ObjectListString.AppendFormat("\t.CompilerOutputPath = \"{0}\"\n", CompilerOutputPath);
				if(UsedUnity)
				{
					ObjectListString.AppendFormat("\t.CompilerInputUnity = {{ {0} }}\n", string.Format("'unity_{0}'", ActionNumber));
				}
				else
				{
					ObjectListString.AppendFormat("\t.CompilerInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", el)).ToArray()));
				}
				ObjectListString.AppendFormat("\t.CompilerOptions = '{0}'\n", CompilerOptions);
				if (!string.IsNullOrEmpty(CompilerOutputExtension))
				{
					ObjectListString.AppendFormat("\t.CompilerOutputExtension = '{0}'\n", CompilerOutputExtension);
				}
				if (!string.IsNullOrEmpty(PrecompiledHeaderString))
				{
					ObjectListString.Append(PrecompiledHeaderString);
				}
				if (!string.IsNullOrEmpty(PreBuildBatchFile))
				{
					ObjectListString.Append("\t.PreBuildDependencies  = 'prebuild'\n");
				}
				ObjectListString.Append("}\n\n");
				ResultString += ObjectListString.ToString();
				return ResultString;
			}
		}

		static private void GenerateBffFromVcxproj(string Config, string Platform)
		{
			Project ActiveProject = CurrentProject.Proj;
			string MD5hash = "wafflepalooza";
			PreBuildBatchFile = "";
			PostBuildBatchFile = "";
			bool FileChanged = HasFileChanged(ActiveProject.FullPath, Platform, Config, out MD5hash);

			string configType = ActiveProject.GetProperty("ConfigurationType").EvaluatedValue;
			switch(configType)
			{
				case "DynamicLibrary": BuildOutput = BuildType.DynamicLib; break;
				case "StaticLibrary": BuildOutput = BuildType.StaticLib; break;
				default:
				case "Application": BuildOutput = BuildType.Application; break;
			}

			PlatformToolsetVersion = ActiveProject.GetProperty("PlatformToolsetVersion").EvaluatedValue;

			string OutDir = ActiveProject.GetProperty("OutDir").EvaluatedValue;
			string IntDir = ActiveProject.GetProperty("IntDir").EvaluatedValue;

			StringBuilder OutputString = new StringBuilder(MD5hash + "\n\n");

			OutputString.AppendFormat(".VSBasePath = '{0}'\n", ActiveProject.GetProperty("VSInstallDir").EvaluatedValue);
			VCBasePath = ActiveProject.GetProperty("VCInstallDir").EvaluatedValue;
			OutputString.AppendFormat(".VCBasePath = '{0}'\n", VCBasePath);

			WindowsSDKTarget = ActiveProject.GetProperty("TargetPlatformVersion") != null ? ActiveProject.GetProperty("TargetPlatformVersion").EvaluatedValue : "8.1";

			OutputString.AppendFormat(".WindowsSDKBasePath = '{0}'\n\n", ActiveProject.GetProperty("WindowsSdkDir").EvaluatedValue);

			OutputString.Append("Settings\n{\n\t.Environment = \n\t{\n");
			OutputString.AppendFormat("\t\t\"INCLUDE={0}\",\n", ActiveProject.GetProperty("IncludePath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"LIB={0}\",\n", ActiveProject.GetProperty("LibraryPath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"LIBPATH={0}\",\n", ActiveProject.GetProperty("ReferencePath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"PATH={0}\"\n", ActiveProject.GetProperty("Path").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"TMP={0}\"\n", ActiveProject.GetProperty("Temp").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"TEMP={0}\"\n", ActiveProject.GetProperty("Temp").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"SystemRoot={0}\"\n", ActiveProject.GetProperty("SystemRoot").EvaluatedValue);
			OutputString.Append("\t}\n}\n\n");

			StringBuilder CompilerString = new StringBuilder("Compiler('msvc')\n{\n");

			string CompilerRoot = VCBasePath + "bin/";
			if (Platform == "Win64" || Platform == "x64")
			{
				CompilerString.Append(string.Format("\t.Root = '{0}'\n", ActiveProject.GetProperty("VC_ExecutablePath_x86_x64").EvaluatedValue));
				CompilerRoot = ActiveProject.GetProperty("VC_ExecutablePath_x86_x64").EvaluatedValue;
			}
			else if (Platform == "Win32" || Platform == "x86" || true) //Hmm.
			{
				CompilerString.Append(string.Format("\t.Root = '{0}'\n", ActiveProject.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue));
				CompilerRoot = ActiveProject.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue;
			}

			if (CommandLineOptions.UseRelativePaths)
			{
				CompilerString.Append("\t.UseRelativePaths_Experimental = true\n");
			}

			if (CommandLineOptions.UseLightCache)
			{
				CompilerString.Append("\t.UseLightCache_Experimental = true\n");
			}

			CompilerString.Append("\t.Executable = '$Root$/cl.exe'\n");
			CompilerString.Append("\t.ExtraFiles =\n\t{\n");
			CompilerString.Append("\t\t'$Root$/c1.dll'\n");
			CompilerString.Append("\t\t'$Root$/c1xx.dll'\n");
			CompilerString.Append("\t\t'$Root$/c2.dll'\n");
			CompilerString.Append("\t\t'$Root$/msobj140.dll'\n");
			CompilerString.Append("\t\t'$Root$/mspdb140.dll'\n");
			CompilerString.Append("\t\t'$Root$/mspdbcore.dll'\n");
			CompilerString.Append("\t\t'$Root$/mspdbsrv.exe'\n");
			CompilerString.Append("\t\t'$Root$/mspft140.dll'\n");
			CompilerString.Append("\t\t'$Root$/msvcp140.dll'\n");
			CompilerString.Append("\t\t'$Root$/msvcp140_atomic_wait.dll'\n");
			CompilerString.Append("\t\t'$Root$/tbbmalloc.dll'\n");
			CompilerString.Append("\t\t'$Root$/vcruntime140.dll'\n");

			//CompilerString.AppendFormat("\t\t'{1}{0}/Microsoft.VC142.CRT/msvcp140.dll'\n", Platform == "Win32" ? "x86" : "x64", ActiveProject.GetProperty("VCToolsRedistInstallDir").EvaluatedValue);
			//CompilerString.AppendFormat("\t\t'{1}{0}/Microsoft.VC142.CRT/vccorlib140.dll'\n", Platform == "Win32" ? "x86" : "x64", ActiveProject.GetProperty("VCToolsRedistInstallDir").EvaluatedValue);

			if (File.Exists(CompilerRoot + "1033/clui.dll")) //Check English first...
			{
				CompilerString.Append("\t\t'$Root$/1033/clui.dll'\n");
			}
			else
			{
				var numericDirectories = Directory.GetDirectories(CompilerRoot).Where(d => Path.GetFileName(d).All(char.IsDigit));
				var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
				if(cluiDirectories.Any())
				{
					CompilerString.AppendFormat("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First()));
				}
			}

			CompilerString.Append("\t}\n"); //End extra files
			CompilerString.Append("}\n\n"); //End compiler

			CompilerString.Append("Compiler('rc')\n{\n");
			CompilerString.Append(string.Format("\t.Executable = '$WindowsSDKBasePath$\\bin\\{0}\\x64\\rc.exe'\n", WindowsSDKTarget));
			CompilerString.Append("\t.CompilerFamily = 'custom'\n");
			CompilerString.Append("}\n\n"); //End rc compiler

			OutputString.Append(CompilerString);

			if (ActiveProject.GetItems("PreBuildEvent").Any())
			{
				var buildEvent = ActiveProject.GetItems("PreBuildEvent").First();
				if (buildEvent.Metadata.Any())
				{
					var mdPi = buildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(mdPi.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" " +
							(Platform == "Win32" ? "x86" : "x64") + " "
							+ WindowsSDKTarget + "\n";
						PreBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_prebuild.bat");
						File.WriteAllText(PreBuildBatchFile, BatchText + mdPi.EvaluatedValue);
						OutputString.Append("Exec('prebuild') \n{\n");
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PreBuildBatchFile + ".txt");
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			string CompilerOptions = "";

			List<ObjectListNode> ObjectLists = new List<ObjectListNode>();
			var CompileItems = ActiveProject.GetItems("ClCompile");
			string PrecompiledHeaderString = "";

			foreach (var Item in CompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
					{
						ToolTask CLtask = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
						CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
						string pchCompilerOptions = GenerateTaskCommandLine(CLtask, new string[] { "PrecompiledHeaderOutputFile", "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
						PrecompiledHeaderString = "\t.PCHOptions = '" + string.Format("\"%1\" /Fp\"%2\" /Fo\"%3\" {0} '\n", pchCompilerOptions);
						PrecompiledHeaderString += "\t.PCHInputFile = '" + Item.EvaluatedInclude + "'\n";
						PrecompiledHeaderString += "\t.PCHOutputFile = '" + Item.GetMetadataValue("PrecompiledHeaderOutputFile") + "'\n";
						break; //Assumes only one pch...
					}
				}
			}

			foreach (var Item in CompileItems)
			{
				bool ExcludePrecompiledHeader = false;
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "NotUsing").Any())
						ExcludePrecompiledHeader = true;
				}

				ToolTask Task = (ToolTask) Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
				Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() }); //CPPTasks throws an exception otherwise...
				string TempCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
				if (Path.GetExtension(Item.EvaluatedInclude) == ".c")
					TempCompilerOptions += " /TC";
				else
					TempCompilerOptions += " /TP";
				CompilerOptions = TempCompilerOptions;
				string FormattedCompilerOptions = string.Format("\"%1\" /Fo\"%2\" {0}", TempCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString));
				if(!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString));
				}
			}

			PrecompiledHeaderString = "";

			var ResourceCompileItems = ActiveProject.GetItems("ResourceCompile");
			foreach (var Item in ResourceCompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
				}

				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC"));
				string ResourceCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ResourceOutputFileName", "DesigntimePreprocessorDefinitions" }, Item.Metadata);

				string formattedCompilerOptions = string.Format("{0} /fo\"%2\" \"%1\"", ResourceCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString));
				if (!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString, ".res"));
				}
			}

			int ActionNumber = 0;
			foreach (ObjectListNode ObjList in ObjectLists)
			{
				OutputString.Append(ObjList.ToString(ActionNumber));
				ActionNumber++;
			}

			if (ActionNumber > 0)
			{
				HasCompileActions = true;
			}
			else
			{
				HasCompileActions = false;
				Console.WriteLine("Project has no actions to compile.");
			}

			string CompileActions = string.Join(",", Enumerable.Range(0, ActionNumber).ToList().ConvertAll(x => string.Format("'action_{0}'", x)).ToArray());

			if (BuildOutput == BuildType.Application || BuildOutput == BuildType.DynamicLib)
			{
				OutputString.AppendFormat("{0}('output')\n{{", BuildOutput == BuildType.Application ? "Executable" : "DLL");
				OutputString.AppendFormat("\t.Linker = '{0}\\link.exe'\n", CompilerRoot);

				var LinkDefinitions = ActiveProject.ItemDefinitions["Link"];
				string OutputFile = LinkDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');

				if(HasCompileActions)
				{
					string DependencyOutputPath = LinkDefinitions.GetMetadataValue("ImportLibrary");
					if (Path.IsPathRooted(DependencyOutputPath))
						DependencyOutputPath = DependencyOutputPath.Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, DependencyOutputPath).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
				string LinkerOptions = GenerateTaskCommandLine(Task, new string[] { "OutputFile", "ProfileGuidedDatabase" }, LinkDefinitions.Metadata);

				if (!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					LinkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LinkerOptions = '\"%1\" /OUT:\"%2\" {0}'\n", LinkerOptions.Replace("'","^'"));
				OutputString.AppendFormat("\t.LinkerOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.Libraries = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}
			else if(BuildOutput == BuildType.StaticLib)
			{
				OutputString.Append("Library('output')\n{");
				OutputString.Append("\t.Compiler = 'msvc'\n");
				OutputString.Append(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c {0}'\n", CompilerOptions));
				OutputString.Append(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntDir));
				OutputString.AppendFormat("\t.Librarian = '{0}\\lib.exe'\n", CompilerRoot);

				var LibDefinitions = ActiveProject.ItemDefinitions["Lib"];
				string OutputFile = LibDefinitions.GetMetadataValue("OutputFile").Replace('\\','/');

				if(HasCompileActions)
				{
					string DependencyOutputPath = "";
					if (Path.IsPathRooted(OutputFile))
						DependencyOutputPath = Path.GetFullPath(OutputFile).Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, OutputFile).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
				string linkerOptions = GenerateTaskCommandLine(task, new string[] { "OutputFile" }, LibDefinitions.Metadata);
				if(!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					linkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LibrarianOptions = '\"%1\" /OUT:\"%2\" {0}'\n", linkerOptions);
				OutputString.AppendFormat("\t.LibrarianOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.LibrarianAdditionalInputs = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}

			if (ActiveProject.GetItems("PostBuildEvent").Any())
			{
				ProjectItem BuildEvent = ActiveProject.GetItems("PostBuildEvent").First();
				if (BuildEvent.Metadata.Any())
				{
					ProjectMetadata MetaData = BuildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(MetaData.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" " +
							   (Platform == "Win32" ? "x86" : "x64") + " "
							   + WindowsSDKTarget + "\n";
						PostBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_postbuild.bat");
						File.WriteAllText(PostBuildBatchFile, BatchText + MetaData.EvaluatedValue);
						OutputString.Append("Exec('postbuild') \n{\n");
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PostBuildBatchFile + ".txt");
						OutputString.Append("\t.PreBuildDependencies = 'output' \n");
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			OutputString.AppendFormat("Alias ('all')\n{{\n\t.Targets = {{ '{0}' }}\n}} ", string.IsNullOrEmpty(PostBuildBatchFile) ? "output" : "postbuild");

			if(FileChanged || CommandLineOptions.AlwaysRegenerate)
			{
				File.WriteAllText(BFFOutputFilePath, OutputString.ToString());
			}
		}

		public static string GenerateTaskCommandLine(
			ToolTask Task,
			string[] PropertiesToSkip,
			IEnumerable<ProjectMetadata> MetaDataList)
		{
			foreach (ProjectMetadata MetaData in MetaDataList)
			{
				if (PropertiesToSkip.Contains(MetaData.Name))
					continue;

				var MatchingProps = Task.GetType().GetProperties().Where(prop => prop.Name == MetaData.Name);
				if (MatchingProps.Any() && !string.IsNullOrEmpty(MetaData.EvaluatedValue))
				{
					string EvaluatedValue = MetaData.EvaluatedValue.Trim();
					if(MetaData.Name == "AdditionalIncludeDirectories")
					{
						EvaluatedValue = EvaluatedValue.Replace("\\\\", "\\");
						EvaluatedValue = EvaluatedValue.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					}

					PropertyInfo propInfo = MatchingProps.First(); //Dubious
					if (propInfo.PropertyType.IsArray && propInfo.PropertyType.GetElementType() == typeof(string))
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue.Split(';'), propInfo.PropertyType));
					}
					else
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue, propInfo.PropertyType));
					}
				}
			}

			var GenCmdLineMethod = Task.GetType().GetRuntimeMethods().Where(meth => meth.Name == "GenerateCommandLine").First(); //Dubious
			return GenCmdLineMethod.Invoke(Task, new object[] { Type.Missing, Type.Missing }) as string;
		}
	}

}
