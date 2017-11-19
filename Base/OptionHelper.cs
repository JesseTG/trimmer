#if !NO_TRIMMER || UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace sttz.Trimmer
{

/// <summary>
/// Helper methods for Options.
/// </summary>
/// <remarks>
/// This static class contains methods to be used by Option subclasses
/// that contain common design patterns or utilities.
/// </remarks>
public static class OptionHelper
{
    // ------ Injection ------

    /// <summary>
    /// Name used for the container that holds the singletons
    /// created by <see cref="GetSingleton*"> and <see cref="InjectFeature*"/>.
    /// </summary>
    const string CONTAINER_NAME = "_Trimmer";

    /// <summary>
    /// Get a singleton script instance in the current scene.
    /// Intended for use in Options' <see cref="Option.Apply"/> methods.
    /// </summary>
    /// <remarks>
    /// This method can be used to implement the feature injection design pattern
    /// in Options (together with <see cref="InjectFeature*"/>). In this case the
    /// Option has an associated feature that isn't statically configured in
    /// the project but instead injected on-demand. The feature must be implemented
    /// as a `MonoBehaviour` script that is not added to any scene but instead
    /// exclusively injected and configured by the Option.
    /// 
    /// This method should be used in an Option's <see cref="Option.Apply"/> method 
    /// to inject the feature when playing in the editor or when running a build
    /// with the Option included.
    /// 
    /// Example for a typical implementation:
    /// ```cs
    /// protected bool Validate()
    /// {
    ///     // Check if the Option is properly configured and/or enabled
    ///     return Value &amp;&amp; !string.IsNullOrEmpty(GetChild&lt;OptionChild&gt;().Value);
    /// }
    /// 
    /// public override void Apply()
    /// {
    ///     base.Apply();
    ///     
    ///     var enable = Validate();
    ///     var singleton = OptionHelper.GetSingleton&lt;MyScript>(enable);
    ///     if (singleton != null) {
    ///         singleton.enabled = enable;
    ///         singleton.option = false;
    ///         singleton.otherOption = GetChild&lt;OptionChild>().Value;
    ///     }
    /// }
    /// ```
    /// 
    /// Here, the Option first checks if it is enabled and properly configured
    /// in `Validate`. This is then passed to GetSingleton, so that it doesn't
    /// create the singleton if the feature is not enabled.
    /// 
    /// GetSingleton will always return the instance if it exists (even if the
    /// feature is disabled, so that it can be turned off when it was enabled
    /// before). Therefore always check if GetSingleton returns a non-null value
    /// and apply the configuration if it does.
    /// 
    /// Singletons created by this method will be stored on the "_Trimmer"
    /// game object, which is marked `DontDestroyOnLoad`, so that singletons
    /// persist over scene loads.
    /// 
    /// Use <see cref="InjectFeature*"/> in <see cref="Option.PostprocessScene"/> to
    /// inject the script into the build if the Option is not included.
    /// </remarks>
    /// <param name="create">Wether to create the script if it does not exists.</param>
    /// <returns>The script or null if <paramref name="create"/> is <c>false</c> and the script doesn't exist</returns>
    public static T GetSingleton<T>(bool create = true) where T : Component
    {
        var container = GameObject.Find(CONTAINER_NAME);
        if (container == null) {
            if (!create) return null;
            container = new GameObject(CONTAINER_NAME);
        }

        var script = container.GetComponent<T>();
        if (script == null) {
            if (!create) return null;
            script = container.AddComponent<T>();
        }

        return script;
    }

    #if UNITY_EDITOR

    /// <summary>
    /// Inject a singleton script in a build.
    /// Intended for use in Options' <see cref="Option.PostprocessScene"/> methods.
    /// </summary>
    /// <remarks>
    /// See <see cref="GetSingleton*"/> for a introductory explanation on how to 
    /// implement the feature injection design pattern.
    /// 
    /// InjectFeature is used in conjunction with GetSingleton. It's needed in case
    /// a build includes an Option's associated feature but not the Option itself.
    /// If the Option is included, it can take care of injecting the feature at
    /// runtime in the build. But if only the feature is included, it needs to be
    /// injected at build-time.
    /// 
    /// This method should be used in an Option's <see cref="Option.PostprocessScene"/> 
    /// to inject the feature into the build at build-time when required.
    /// 
    /// Example for a typical implementation:
    /// ```cs
    /// protected bool Validate()
    /// {
    ///     // Check if the Option is properly configured and/or enabled
    ///     return Value &amp;&amp; !string.IsNullOrEmpty(GetChild&lt;OptionChild&gt;().Value);
    /// }
    /// 
    /// #if UNITY_EDITOR
    /// 
    /// override public bool ShouldIncludeOnlyFeature()
    /// {
    ///     // Removes the feature if it's improperly configured.
    ///     // Otherwise the fature will always be incldued even if misconfigrued/disabled.
    ///     return Validate();
    /// }
    /// 
    /// override public void PostprocessScene(Scene scene, OptionInclusion inclusion)
    /// {
    ///     base.PostprocessScene(scene, inclusion);
    /// 
    ///     var singleton = OptionHelper.InjectFeature&lt;MyScript&gt;(scene, inclusion);
    ///     if (singleton != null) {
    ///         singleton.option = false;
    ///         singleton.otherOption = GetChild&lt;OptionChild&gt;().Value;
    ///     }
    /// }
    /// 
    /// #endif
    /// ```
    /// 
    /// Here overriding ShouldIncludeOnlyFeature takes care of checking if only
    /// the feature is included and removing it when it's not enabled/configured.
    /// InjectFeature then adds the singleton to the first scene so that it will
    /// be loaded in the build.
    /// </remarks>
    /// <param name="scene">Pass in the `scene` parameter from <see cref="Option.PostprocessScene"/></param>
    /// <param name="inclusion">Pass in the `inclusion` parameter from <see cref="Option.PostprocessScene"/></param>
    /// <returns>The script if it's injected or null</returns>
    public static T InjectFeature<T>(Scene scene, OptionInclusion inclusion) where T : Component
    {
        // We only inject when the feature is included but the Option is not
        if (inclusion != OptionInclusion.Feature)
            return null;

        // We only inject to the first scene, because DontDestroyOnLoad is set,
        // the script will persist through scene loads
        if (scene.buildIndex != 0)
            return null;

        return GetSingleton<T>(true);
    }

    /// <summary>
    /// Run an external process/script with given arguments and wait for it to exit.
    /// </summary>
    /// <param name="path">Path to the script (absolute, relative to project directory or on the PATH)</param>
    /// <param name="arguments">Arguments to pass to the script</param>
    /// <returns>`true` if the script runs successfully, `false` on error (details will be logged)</returns>
    public static bool RunScript(string path, string arguments)
    {
        string output;
        return RunScript(path, arguments, out output);
    }

    /// <summary>
    /// Run an external process/script with given arguments, wait for it to exit
    /// and capture its output.
    /// </summary>
    /// <param name="path">Path to the script (absolute, relative to project directory or on the PATH)</param>
    /// <param name="arguments">Arguments to pass to the script</param>
    /// <param name="output">The standard output of the script</param>
    /// <returns>`true` if the script runs successfully, `false` on error (details will be logged)</returns>
    public static bool RunScript(string path, string arguments, out string output)
    {
        output = null;

        if (string.IsNullOrEmpty(path)) {
            Debug.LogError("RunScript: path null or empty");
            return false;
        }

        var scriptName = Path.GetFileName(path);
        var script = new System.Diagnostics.Process();
        script.StartInfo.UseShellExecute = false;
        script.StartInfo.RedirectStandardOutput = true;
        script.StartInfo.RedirectStandardError = true;
        script.StartInfo.FileName = path;
        script.StartInfo.Arguments = arguments;

        try {
            script.Start();
            script.WaitForExit();
        } catch (Exception e) {
            Debug.LogError("RunScript: Exception running " + scriptName + ": " + e.Message);
            return false;
        }

        output = script.StandardOutput.ReadToEnd();

        if (script.ExitCode != 0) {
            Debug.LogError("RunScript: " + scriptName + " returned error: " + script.StandardError.ReadToEnd());
            return false;
        }

        return true;
    }

    #endif

    // -------- Plugin Removal --------

    #if UNITY_EDITOR

    class PluginDescription
    {
        public string[] deployPaths;
        public string[] extensions;
    }

    static PluginDescription pluginsOSX = new PluginDescription() {
        deployPaths = new string[] { "Contents/Plugins" },
        extensions = new string[] { ".bundle" }
    };
    static PluginDescription pluginsWindows = new PluginDescription() {
        deployPaths = new string[] {
            "",
            "{Product}_Data/Plugins", 
        },
        extensions = new string[] { ".dll" }
    };
    static PluginDescription pluginsLinux = new PluginDescription() {
        deployPaths = new string[] { 
            "{Product}_Data/Plugins", 
            "{Product}_Data/Plugins/x86", 
            "{Product}_Data/Plugins/x86_64", 
        },
        extensions = new string[] { ".so" }
    };

    static Dictionary<BuildTarget, PluginDescription> pluginDescs
        = new Dictionary<BuildTarget, PluginDescription>() {

        { BuildTarget.StandaloneOSXIntel, pluginsOSX },
        { BuildTarget.StandaloneOSXIntel64, pluginsOSX },
        { BuildTarget.StandaloneOSXUniversal, pluginsOSX },

        { BuildTarget.StandaloneWindows, pluginsWindows },
        { BuildTarget.StandaloneWindows64, pluginsWindows },

        { BuildTarget.StandaloneLinux, pluginsLinux },
        { BuildTarget.StandaloneLinux64, pluginsLinux },
        { BuildTarget.StandaloneLinuxUniversal, pluginsLinux },
    };

    /// <summary>
    /// Remove a plugin from the build.
    /// </summary>
    /// <remarks>
    /// A feature that an Option is configuring might use native plugins that Unity
    /// always includes in builds that the plugin supports. Depending on the Option's
    /// configuration, the feature using the native plugins might be removed completely
    /// but the native plugins will still remain in the build.
    /// 
    /// This helper method can be used by Options to remove plugins from builds after
    /// the fact, i.e. in the Option's <see cref="Option.PostprocessBuild*"/>
    /// callback.
    /// 
    /// Note that this method currently only supports removing plugins from standalone
    /// build targets.
    /// </remarks>
    public static void RemovePluginFromBuild(BuildTarget target, string pathToBuiltProject, Regex pluginNameMatch)
    {
        // TODO: Check out Unity 2017.2's PluginImporter.SetIncludeInBuildDelegate,
        // which could potentially replace this functionality

        PluginDescription desc;
        if (!pluginDescs.TryGetValue(target, out desc)) {
            Debug.LogError(string.Format("Build target {0} not supported for plugin removal.", target));
            return;
        }

        if (File.Exists(pathToBuiltProject)) {
            pathToBuiltProject = System.IO.Path.GetDirectoryName(pathToBuiltProject);
        }

        foreach (var pathTemplate in desc.deployPaths) {
            var path = pathTemplate.Replace("{Product}", PlayerSettings.productName);
            path = System.IO.Path.Combine(pathToBuiltProject, path);

            if (!Directory.Exists(path)) {
                Debug.Log("Plugin path does not exist: " + path);
                continue;
            }

            foreach (var entry in Directory.GetFileSystemEntries(path)) {
                var extension = System.IO.Path.GetExtension(entry);
                if (!desc.extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) {
                    Debug.Log("Extension does not match: " + entry);
                    continue;
                }

                var fileName = System.IO.Path.GetFileNameWithoutExtension(entry);
                if (!pluginNameMatch.IsMatch(fileName)) {
                    Debug.Log("Name does not match: " + entry);
                    continue;
                }

                Debug.Log("Removing plugin: " + entry);
                if (File.Exists(entry))
                    File.Delete(entry);
                else
                    Directory.Delete(entry, true);
            }
        }
    }

    #endif
}

}

#endif
