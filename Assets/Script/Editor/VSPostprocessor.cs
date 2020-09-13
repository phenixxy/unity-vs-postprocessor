using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
#if ENABLE_VSTU
using SyntaxTree.VisualStudio.Unity.Bridge;
#endif

public class UTF8StringWriter : StringWriter
{
	public override Encoding Encoding { get { return Encoding.UTF8; } }
}

public class AssemblyDefinition
{
	public string name;
	public List<string> references;
	public List<string> includePlatforms;
	public List<string> excludePlatforms;
	public bool allowUnsafeCode;
	public bool autoReferenced;
	public bool overrideReferences;
	public List<string> precompiledReferences;
	public List<string> defineConstraints;
	public List<string> optionalUnityReferences;

	public AssemblyDefinition(string path)
	{
		var text = File.ReadAllText(path);
		EditorJsonUtility.FromJsonOverwrite(text, this);
	}

	public bool IsValidPlatform(string platform, string target)
	{
		if (includePlatforms != null)
		{
			return includePlatforms.Any(p => p.Contains(platform)) || includePlatforms.Contains(target);
		}
		if (excludePlatforms != null)
		{
			return !excludePlatforms.Any(p => p.Contains(platform)) && !excludePlatforms.Contains(target);
		}
		return true;
	}
}

[InitializeOnLoad]
public class VSPostprocessor : AssetPostprocessor
{
	private static string[] _Platforms = new string[] { "Windows", "iOS", "Android" };
	private static string[] _Targets = new string[] { "Editor", "Player" };
	private static string[] _Configs = new string[] { "Clean", "Custom" };

	private const string _ConfigPrefix = "vs_";
	private const int _WarningLevel = 4;
	private const bool _WarningAsError = false;

#if ENABLE_VSTU
	static VSPostprocessor()
	{
		ProjectFilesGenerator.SolutionFileGeneration -= ProcessSolutionFile;
		ProjectFilesGenerator.SolutionFileGeneration += ProcessSolutionFile;
		ProjectFilesGenerator.ProjectFileGeneration -= ProcessProjectFile;
		ProjectFilesGenerator.ProjectFileGeneration += ProcessProjectFile;
	}
#else
	public string OnGeneratedSlnSolution(string path, string content)
	{
		return ProcessSolutionFile(path, content);
	}

	public string OnGeneratedCSProject(string path, string content)
	{
		return ProcessProjectFile(path, content);
	}
#endif

	private static string ProcessSolutionFile(string path, string content)
	{
		try
		{
			var writer = new StringWriter();
			var projects = new List<KeyValuePair<string, string>>();

			var reader = new StringReader(content);
			while (reader.Peek() != -1)
			{
				var line = reader.ReadLine();
				if (line.StartsWith("Project"))
				{
					var values = line.Split('=')[1].Split(',');
					var name = values[0].Trim('"', ' ');
					var hash = values[2].Trim('"', ' ');
					projects.Add(new KeyValuePair<string, string>(name, hash));
				}
				else if (line == "Global")
				{
					break;
				}
				writer.WriteLine(line);
			}

			writer.WriteLine("Global");
			writer.WriteLine("	GlobalSection(SolutionConfigurationPlatforms) = preSolution");
			writer.WriteLine("		Debug|Any CPU = Debug|Any CPU");
			foreach (var platform in _Platforms)
			{
				foreach (var target in _Targets)
				{
					foreach (var config in _Configs)
					{
						writer.WriteLine("		{0}|Any CPU = {0}|Any CPU", GetConfigName(platform, target, config));
					}
				}
			}
			writer.WriteLine("	EndGlobalSection");
			writer.WriteLine("	GlobalSection(ProjectConfigurationPlatforms) = postSolution");
			foreach (var project in projects)
			{
				writer.WriteLine("		{0}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", project.Value);
				writer.WriteLine("		{0}.Debug|Any CPU.Build.0 = Debug|Any CPU", project.Value);
				foreach (var platform in _Platforms)
				{
					foreach (var target in _Targets)
					{
						foreach (var config in _Configs)
						{
							if (IsValidConfig(project.Key, platform, target, config))
							{
								writer.WriteLine("		{0}.{1}|Any CPU.ActiveCfg = {1}|Any CPU", project.Value, GetConfigName(platform, target, config));
								writer.WriteLine("		{0}.{1}|Any CPU.Build.0 = {1}|Any CPU", project.Value, GetConfigName(platform, target, config));
							}
							else
							{
								writer.WriteLine("		{0}.{1}|Any CPU.ActiveCfg = Debug|Any CPU", project.Value, GetConfigName(platform, target, config));
							}
						}
					}
				}
			}
			writer.WriteLine("	EndGlobalSection");
			writer.WriteLine("	GlobalSection(SolutionProperties) = preSolution");
			writer.WriteLine("		HideSolutionNode = FALSE");
			writer.WriteLine("	EndGlobalSection");
			writer.WriteLine("EndGlobal");

			return writer.ToString();
		}
		catch (Exception ex)
		{
			Debug.LogErrorFormat("failed to process solution file: {0}\n{1}", path, ex.ToString());
			return content;
		}
	}

	private static string ProcessProjectFile(string path, string content)
	{
		try
		{
			var projectName = Path.GetFileNameWithoutExtension(path);

			var document = XDocument.Parse(content);
			var ns = document.Root.GetDefaultNamespace();
			var groups = document.Root.Elements(ns + "PropertyGroup");
			var debug = groups.First(e => e.HasElements && e.Element(ns + "Configuration") != null);
			var debugAnyCPU = groups.First(e => e.HasAttributes && e.Attribute("Condition").Value.Contains("'Debug|AnyCPU'"));
			var releaseAnyCPU = groups.First(e => e.HasAttributes && e.Attribute("Condition").Value.Contains("'Release|AnyCPU'"));

			// delete custom groups
			foreach (var group in groups)
			{
				if (group.HasAttributes && group.Attribute("Condition").Value.Contains("'" + _ConfigPrefix))
				{
					group.Remove();
				}
			}

			// warning settings
			SetWarningLevel(debug, debugAnyCPU, _WarningLevel);
			SetWarningAsError(debug, debugAnyCPU, _WarningAsError);
			SetIgnoreWarnings(debug, debugAnyCPU);

			// all configs
			foreach (var platform in _Platforms)
			{
				foreach (var target in _Targets)
				{
					foreach (var config in _Configs)
					{
						if (IsValidConfig(projectName, platform, target, config))
						{
							releaseAnyCPU.AddBeforeSelf(CreatePropertyGroup(platform, target, config, debugAnyCPU));
						}
					}
				}
			}

			// set assembly references by platform
			if (!IsEditorProject(projectName))
			{
				var references = document.Root.Descendants(ns + "Reference");
				foreach (var reference in references)
				{
					var hintPath = reference.Element(ns + "HintPath");
					if (hintPath == null)
					{
						continue;
					}
					var dllPath = hintPath.Value.Replace('\\', '/');
					if (dllPath.Contains("VisualStudio") ||
						dllPath.Contains("NetStandard"))
					{
						continue;
					}
					if (dllPath.Contains("UnityEditor") ||
						dllPath.Contains(".Editor"))
					{
						var editorCondition = new StringBuilder("'$(Configuration)' == 'Debug'");
						foreach (var platform in _Platforms)
						{
							if (dllPath.Contains("/iOSSupport/") && platform != "iOS")
							{
								continue;
							}
							foreach (var config in _Configs)
							{
								editorCondition.AppendFormat(" Or '$(Configuration)' == '{0}'", GetConfigName(platform, "Editor", config));
							}
						}
						reference.SetAttributeValue("Condition", editorCondition.ToString());
					}
					else if (dllPath.Contains("/Assets/"))
					{
						var excludePlatforms = GetPluginExcludePlatforms(dllPath);
						if (excludePlatforms.Count == 0)
						{
							continue;
						}
						var condition = new StringBuilder("'$(Configuration)' == 'Debug'");
						foreach (var platform in _Platforms)
						{
							foreach (var target in _Targets)
							{
								if (excludePlatforms.Contains(target))
								{
									continue;
								}
								if (target != "Editor" && excludePlatforms.Contains(platform))
								{
									continue;
								}
								foreach (var config in _Configs)
								{
									condition.AppendFormat(" Or '$(Configuration)' == '{0}'", GetConfigName(platform, target, config));
								}
							}
						}
						reference.SetAttributeValue("Condition", condition.ToString());
					}
				}
			}

			// set project references by platform
			var projectReferences = document.Root.Descendants(ns + "ProjectReference");
			foreach (var reference in projectReferences)
			{
				var name = reference.Element(ns + "Name").Value;
				var condition = new StringBuilder("'$(Configuration)' == 'Debug'");
				var setCondition = false;
				foreach (var platform in _Platforms)
				{
					foreach (var target in _Targets)
					{
						foreach (var config in _Configs)
						{
							if (IsValidConfig(name, platform, target, config))
							{
								condition.AppendFormat(" Or '$(Configuration)' == '{0}'", GetConfigName(platform, target, config));
							}
							else
							{
								setCondition = true;
							}
						}
					}
				}
				if (setCondition)
				{
					reference.SetAttributeValue("Condition", condition.ToString());
				}
			}

			// remove "Release|AnyCPU"
			releaseAnyCPU.Remove();

			var writer = new UTF8StringWriter();
			document.Save(writer);
			return writer.ToString();
		}
		catch (Exception ex)
		{
			Debug.LogErrorFormat("failed to process project file: {0}\n{1}", path, ex.ToString());
			return content;
		}
	}

	#region Utilities
	private static string GetConfigName(string platform, string target, string config)
	{
		return $"{_ConfigPrefix}{platform}_{target}_{config}";
	}

	private static bool IsValidConfig(string project, string platform, string target, string config)
	{
		if (IsEditorProject(project) && target != "Editor")
		{
			return false;
		}

		if (IsPackageProject(project))
		{
			return false;
		}

		var asmdef = GetAssemblyDefinition(project);
		if (asmdef != null && !asmdef.IsValidPlatform(platform, target))
		{
			return false;
		}

		return true;
	}

	private static bool IsEditorProject(string project)
	{
		var assembly = GetAssembly(project);
		if (assembly != null && assembly.flags == AssemblyFlags.EditorAssembly)
		{
			return true;
		}
		if (project.Contains(".Editor"))
		{
			return true;
		}
		return false;
	}

	private static bool IsPackageProject(string project)
	{
		var assembly = GetAssembly(project);
		if (assembly != null && assembly.sourceFiles.Length > 0)
		{
			var file = assembly.sourceFiles[0];
			if (file.StartsWith(@"Packages/"))
			{
				return true;
			}
		}
		return false;
	}

	private static XElement CreatePropertyGroup(string platform, string target, string config, XElement template)
	{
		var ns = template.Name.Namespace;
		var element = new XElement(template);
		element.Attribute("Condition").Value = string.Format(" '$(Configuration)|$(Platform)' == '{0}|AnyCPU' ", GetConfigName(platform, target, config));
		element.Element(ns + "OutputPath").Value = string.Format(@"Temp\bin\{0}\", GetConfigName(platform, target, config));
		element.Element(ns + "DefineConstants").Value = string.Join(";", GetDefines(platform, target, config, template.Element(ns + "DefineConstants").Value.Split(';')));
		return element;
	}

	private static string[] GetDefines(string platform, string target, string config, IEnumerable<string> defines)
	{
		var set = new HashSet<string>();
		foreach (var define in defines)
		{
			if (platform != "Windows" && (define.Contains("_WIN") || define.Contains("_STANDALONE")))
			{
				continue;
			}
			if (platform != "iOS" && (define.Contains("_IOS") || define.Contains("_IPHONE") || define.Contains("_OSX")))
			{
				continue;
			}
			if (platform != "Android" && define.Contains("_ANDROID"))
			{
				continue;
			}
			if (target != "Editor" && define.Contains("_EDITOR"))
			{
				continue;
			}
			set.Add(define);
		}

		var customDefines = PlayerSettings.GetScriptingDefineSymbolsForGroup(GetBuildTargetGroup(platform)).Split(';');
		if (config == "Custom")
		{
			set.UnionWith(customDefines);
		}
		else
		{
			set.ExceptWith(customDefines);
		}

		switch (platform)
		{
			case "Windows":
				set.Add("UNITY_STANDALONE");
				set.Add("UNITY_STANDALONE_WIN");
				break;
			case "iOS":
				set.Add("UNITY_IOS");
				set.Add("UNITY_IPHONE");
				set.Add("UNITY_IPHONE_API");
				break;
			case "Android":
				set.Add("UNITY_ANDROID");
				set.Add("UNITY_ANDROID_API");
				break;
		}

		if (target == "Editor")
		{
			set.Add("UNITY_EDITOR");
			set.Add("UNITY_EDITOR_64");
			if (platform == "iOS")
			{
				set.Add("UNITY_EDITOR_OSX");
			}
			else
			{
				set.Add("UNITY_EDITOR_WIN");
			}
		}

		return set.ToArray();
	}

	private static BuildTargetGroup GetBuildTargetGroup(string platform)
	{
		switch (platform)
		{
			case "Windows": return BuildTargetGroup.Standalone;
			case "Android": return BuildTargetGroup.Android;
			case "iOS": return BuildTargetGroup.iOS;
		}
		return EditorUserBuildSettings.selectedBuildTargetGroup;
	}

	private static List<string> GetPluginExcludePlatforms(string path)
	{
		var platforms = new List<string>();
		if (!path.StartsWith("Assets/"))
		{
			var p = path.IndexOf("/Assets/");
			path = path.Substring(p + 1);
		}
		var importer = AssetImporter.GetAtPath(path) as PluginImporter;
		if (importer != null)
		{
			if (importer.GetCompatibleWithAnyPlatform())
			{
				if (importer.GetExcludeEditorFromAnyPlatform())
				{
					platforms.Add("Editor");
				}
				if (importer.GetExcludeFromAnyPlatform(BuildTarget.StandaloneWindows))
				{
					platforms.Add("Windows");
				}
				if (importer.GetExcludeFromAnyPlatform(BuildTarget.iOS))
				{
					platforms.Add("iOS");
				}
				if (importer.GetExcludeFromAnyPlatform(BuildTarget.Android))
				{
					platforms.Add("Android");
				}
			}
			else
			{
				if (!importer.GetCompatibleWithEditor())
				{
					platforms.Add("Editor");
				}
				if (!importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows))
				{
					platforms.Add("Windows");
				}
				if (!importer.GetCompatibleWithPlatform(BuildTarget.iOS))
				{
					platforms.Add("iOS");
				}
				if (!importer.GetCompatibleWithPlatform(BuildTarget.Android))
				{
					platforms.Add("Android");
				}
			}
		}
		return platforms;
	}
	#endregion

	#region Warning Settings
	private static void SetWarningLevel(XElement debug, XElement debugAnyCPU, int level)
	{
		var ns = debug.GetDefaultNamespace();

		// set in "Debug"
		var element = debug.Element(ns + "WarningLevel");
		if (element == null)
		{
			element = new XElement(ns + "WarningLevel");
			debug.LastNode.AddAfterSelf(element);
		}
		element.Value = level.ToString();

		// remove from "Debug|AnyCPU"
		debugAnyCPU.Element(ns + "WarningLevel")?.Remove();
	}

	private static void SetWarningAsError(XElement debug, XElement debugAnyCPU, bool value)
	{
		var ns = debug.GetDefaultNamespace();

		// set in "Debug"
		var element = debug.Element(ns + "TreatWarningsAsErrors");
		if (element == null)
		{
			element = new XElement(ns + "TreatWarningsAsErrors");
			debug.LastNode.AddAfterSelf(element);
		}
		element.Value = value.ToString();

		// remove from "Debug|AnyCPU"
		debugAnyCPU.Element(ns + "TreatWarningsAsErrors")?.Remove();
	}

	private static void SetIgnoreWarnings(XElement debug, XElement debugAnyCPU)
	{
		// get ignore warnings from mcs.rsp file
		var warnings = GetIgnoreWarnings();

		var ns = debug.GetDefaultNamespace();
		var noWarn = debugAnyCPU.Element(ns + "NoWarn");
		if (noWarn != null)
		{
			// combine warnings in "Debug|AnyCPU"
			warnings.UnionWith(noWarn.Value.Split(','));

			// remove from "Debug|AnyCPU"
			noWarn.Remove();
		}

		// set in "Debug"
		if (warnings.Count > 0)
		{
			var element = debug.Element(ns + "NoWarn");
			if (element == null)
			{
				element = new XElement(ns + "NoWarn");
				debug.LastNode.AddAfterSelf(element);
			}
			element.Value = string.Join(",", warnings.ToArray());
		}
	}

	private static HashSet<string> GetIgnoreWarnings()
	{
		var warnings = new HashSet<string>();
		var path = Path.Combine(Application.dataPath, "mcs.rsp");
		if (File.Exists(path))
		{
			var lines = File.ReadAllLines(path);
			foreach (var line in lines)
			{
				if (line.StartsWith("-nowarn:"))
				{
					warnings.Add(line.Substring("-nowarn:".Length));
				}
			}
		}
		return warnings;
	}
	#endregion

	#region Assemblies
	private static Dictionary<string, Assembly> _Assemblies;
	private static Dictionary<string, AssemblyDefinition> _AssemblyDefinitions;

	private static void InitAssemblies()
	{
		var assemblies = CompilationPipeline.GetAssemblies();
		_Assemblies = new Dictionary<string, Assembly>(assemblies.Length);
		_AssemblyDefinitions = new Dictionary<string, AssemblyDefinition>(assemblies.Length);
		foreach (var assembly in assemblies)
		{
			_Assemblies.Add(assembly.name, assembly);
			var path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
			if (!string.IsNullOrEmpty(path))
			{
				var asmdef = new AssemblyDefinition(path);
				_AssemblyDefinitions.Add(assembly.name, asmdef);
			}
		}
	}

	private static Assembly GetAssembly(string name)
	{
		if (_Assemblies == null)
			InitAssemblies();

		_Assemblies.TryGetValue(name, out var value);
		return value;
	}

	private static AssemblyDefinition GetAssemblyDefinition(string name)
	{
		if (_AssemblyDefinitions == null)
			InitAssemblies();

		_AssemblyDefinitions.TryGetValue(name, out var value);
		return value;
	}
	#endregion
}
