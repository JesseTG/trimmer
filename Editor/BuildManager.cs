﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;

using UnityEngine;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Build;
using UnityEngine.SceneManagement;
using System.Reflection;
using sttz.Workbench.Extensions;

namespace sttz.Workbench.Editor
{

/// <summary>
/// The build manager defines which profile is used for builds and 
/// manages the build process.
/// </summary>
public class BuildManager : IProcessScene, IPreprocessBuild, IPostprocessBuild
{
	// -------- Active Profile --------

	/// <summary>
	/// Platform name used to save project-specific settings.
	/// </summary>
	public const string SettingsPlatformName = "Workbench";
	/// <summary>
	/// Key used to save the active profile GUID.
	/// </summary>
	public const string ActiveProfileGUIDKey = "ActiveProfileGUID";
	/// <summary>
	/// Key used to save the editor source profile GUID.
	/// </summary>
	public const string SourceProfileGUIDKey = "SourceProfileGUID";


	/// <summary>
	/// The active profile, which is used for regular Unity builds.
	/// </summary>
	/// <remarks>
	/// The active profile is saved per-project in the editor profile's ini
	/// file (usually <c>Editor.ini</c> in the project folder).
	/// </remarks>
	public static BuildProfile ActiveProfile {
		get {
			if (_activeProfile == null) {
				var guid = EditorUserBuildSettings.GetPlatformSettings(SettingsPlatformName, ActiveProfileGUIDKey);
				if (string.IsNullOrEmpty(guid))
					return null;

				_activeProfile = LoadAssetByGUID<BuildProfile>(guid);
			}
			return _activeProfile;
		}
		set {
			if (value == _activeProfile)
				return;

			if (value == null) {
				EditorUserBuildSettings.SetPlatformSettings(SettingsPlatformName, ActiveProfileGUIDKey, null);
				return;
			}

			var guid = GetAssetGUID(value);
			if (string.IsNullOrEmpty(guid))
				return;

			EditorUserBuildSettings.SetPlatformSettings(SettingsPlatformName, ActiveProfileGUIDKey, guid);

			_activeProfile = value;
		}
	}
	private static BuildProfile _activeProfile;

	/// <summary>
	/// Profile providing the current configuration for the editor.
	/// </summary>
	/// <remarks>
	/// Instead of using the editor's unique configuration values, it's
	/// possible to use a build profile's configuration instead, allowing to 
	/// quickly switch between sets of configuration values.
	/// </remarks>
	/// <value>
	/// <c>null</c> when using the editor's own configuration, otherwise the 
	/// build profile whose configuration is used.
	/// </value>
	public static BuildProfile EditorSourceProfile {
		get {
			if (_editorSourceProfile == null) {
				var guid = EditorUserBuildSettings.GetPlatformSettings(SettingsPlatformName, SourceProfileGUIDKey);
				if (!string.IsNullOrEmpty(guid)) {
					_editorSourceProfile = BuildManager.LoadAssetByGUID<BuildProfile>(guid);
				}
			}
			return _editorSourceProfile;
		}
		set {
			if (_editorSourceProfile == value)
				return;
			
			var previousValue = _editorSourceProfile;
			_editorSourceProfile = value;

			var guid = string.Empty;
			if (value != null)
				guid = BuildManager.GetAssetGUID(value);

			EditorUserBuildSettings.SetPlatformSettings(SettingsPlatformName, SourceProfileGUIDKey, guid);

			if (Application.isPlaying) {
				if (previousValue == null) {
					// When switching away from editor profile in play mode,
					// we need to save the changes made to the options
					RuntimeProfile.Main.SaveToStore();
					EditorProfile.SharedInstance.store = RuntimeProfile.Main.Store;
				}
				CreateOrUpdateMainRuntimeProfile();
				RuntimeProfile.Main.Apply();
			}
		}
	}
	private static BuildProfile _editorSourceProfile;

	/// <summary>
	/// The profile used for the current build.
	/// </summary>
	/// <remarks>
	/// This allows to temporarily overwrite the active profile.
	/// Set the current profile to null to revert to the active profile.
	/// The current profile is not saved, it will be reset after
	/// script compilation or opening/closing the project.
	/// </remarks>
	public static BuildProfile CurrentProfile {
		get {
			return _currentProfile ?? ActiveProfile;
		}
		set {
			_currentProfile = value;
		}
	}
	private static BuildProfile _currentProfile;

	/// <summary>
	/// Show the active build profile in the inspector.
	/// </summary>
	[MenuItem("Window/Active Build Profile %&b")]
	public static void OpenEditorProfile()
	{
		Selection.activeObject = ActiveProfile;
	}

	[MenuItem("Window/Active Build Profile %&b", true)]
	static bool ValidateOpenEditorProfile()
	{
		return ActiveProfile != null;
	}

	// -------- GUID Helper Methods --------

	/// <summary>
	/// Helper method to get the GUID of an asset object.
	/// </summary>
	/// <returns>
	/// The GUID or null if the object has no GUID (is not an asset).
	/// </returns>
	public static string GetAssetGUID(UnityEngine.Object target)
	{
		var path = AssetDatabase.GetAssetPath(target);
		if (string.IsNullOrEmpty(path))
			return null;

		var guid = AssetDatabase.AssetPathToGUID(path);
		if (string.IsNullOrEmpty(guid))
			return null;

		return guid;
	}

	/// <summary>
	/// Load an asset by its GUID.
	/// </summary>
	/// <returns>
	/// The object of given type in the asset with the given GUID or null
	/// if either no asset with this GUID exists or the asset does not contain
	/// an object of given type.
	/// </returns>
	public static T LoadAssetByGUID<T>(string guid) where T : UnityEngine.Object
	{
		if (string.IsNullOrEmpty(guid))
			return null;

		var path = AssetDatabase.GUIDToAssetPath(guid);
		if (string.IsNullOrEmpty(path))
			return null;

		return AssetDatabase.LoadAssetAtPath(path, typeof(T)) as T;
	}

	// -------- Build Settings Tracking --------

	private static BuildTarget lastBuildTarget;
	private static bool lastDevelopmentBuild;

	// -------- Building --------

	/// <summary>
	/// Populate the <c>BuildPlayerOptions</c> with default values.
	/// </summary>
	public static BuildPlayerOptions GetDefaultOptions(BuildTarget target)
	{
		// TODO: Use BuildPlayerWindow.DefaultBuildMethods.GetBuildPlayerOptions in 2017.2?
		var playerOptions = new BuildPlayerOptions();
		playerOptions.target = target;
		playerOptions.targetGroup = BuildPipeline.GetBuildTargetGroup(target);

		playerOptions.scenes = EditorBuildSettings.scenes
			.Where(s => s.enabled)
			.Select(s => s.path)
			.ToArray();

		playerOptions.options = BuildOptions.None;

		return playerOptions;
	}

	/// <summary>
	/// Show a dialog to let the user pick a build location.
	/// </summary>
	/// <remarks>
	/// Base on BuildPlayerWindow.PickBuildLocation in private Unity engine code.
	/// </remarks>
	public static string PickBuildLocation(BuildTarget target)
	{
		var buildLocation = EditorUserBuildSettings.GetBuildLocation(target);
		
		if (target == BuildTarget.Android && EditorUserBuildSettings.exportAsGoogleAndroidProject) {
			var location = EditorUtility.SaveFolderPanel("Export Google Android Project", buildLocation, "");
			EditorUserBuildSettings.SetBuildLocation(target, location);
			return location;
		}

		string directory = "", filename = "";
		if (!string.IsNullOrEmpty(buildLocation)) {
			directory = Path.GetDirectoryName(buildLocation);
			filename = Path.GetFileName(buildLocation);
		}

		// Call internal method:
		// string SaveBuildPanel(BuildTarget target, string title, string directory, string defaultName, string extension, out bool updateExistingBuild)
		var method = typeof(EditorUtility).GetMethod("SaveBuildPanel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
		if (method == null) {
			Debug.LogError("Could no find SaveBuildPanel method on EditorUtility class.");
			return null;
		}

		var args = new object[] { target, "Build " + target, directory, filename, "", null };
		var path = (string)method.Invoke(null, args);

		return path;
	}

	/// <summary>
	/// Build a profile for its default target and with the default build options.
	/// </summary>
	public static string Build(BuildProfile profile)
	{
		foreach (var target in profile.BuildTargets) {
			var options = GetDefaultOptions(target);
			var error = Build(profile, options);
			if (!string.IsNullOrEmpty(error)) {
				return error;
			}
		}
		return null;
	}

	/// <summary>
	/// Build a profile with the given build options.
	/// </summary>
	/// <remarks>
	/// Note that the <c>BuildPlayerOptions</c> will be passed through the profile's
	/// options' <see cref="Option.PrepareBuild"/>, which can modify it before
	/// the build is started.<br />
	/// Note that if you do not set <c>options.locationPathName</c> and no option sets
	/// it in the <c>PrepareBuild</c> callback, then a save dialog will be shown.
	/// </remarks>
	public static string Build(BuildProfile buildProfile, BuildPlayerOptions options)
	{
		// Prepare build
		BuildManager.CurrentProfile = buildProfile;

		// Run options' PrepareBuild
		CreateOrUpdateBuildOptionsProfile();
		foreach (var option in buildOptionsProfile.OrderBy(o => o.PostprocessOrder)) {
			if ((option.Capabilities & OptionCapabilities.ConfiguresBuild) == 0) continue;
			var inclusion = buildProfile == null ? OptionInclusion.Remove : buildProfile.GetInclusionOf(option);
			options = option.PrepareBuild(options, inclusion);
		}

		// Ask for location if none has been set
		if (string.IsNullOrEmpty(options.locationPathName)) {
			options.locationPathName = PickBuildLocation(options.target);
			if (string.IsNullOrEmpty(options.locationPathName)) {
				return "Cancelled build location dialog";
			}
			EditorUserBuildSettings.SetBuildLocation(options.target, options.locationPathName);
		}

		// Make sure the path has the right extension
		// Call internal method:
		// string PostprocessBuildPlayer.GetExtensionForBuildTarget(BuildTargetGroup targetGroup, BuildTarget target, BuildOptions options)
		var PostprocessBuildPlayer = typeof(BuildPipeline).Assembly.GetType("UnityEditor.PostprocessBuildPlayer");
		if (PostprocessBuildPlayer == null) {
			Debug.LogWarning("Could not find PostprocessBuildPlayer to determine build file extension.");
		} else {
			var GetExtensionForBuildTarget = PostprocessBuildPlayer.GetMethod("GetExtensionForBuildTarget", BindingFlags.Public | BindingFlags.Static);
			if (GetExtensionForBuildTarget == null) {
				Debug.LogWarning("Could not find GetExtensionForBuildTarget to determine build file extension.");
			} else {
				var args = new object[] { options.targetGroup, options.target, options.options };
				var ext = (string)GetExtensionForBuildTarget.Invoke(null, args);

				var current = Path.GetExtension(options.locationPathName);
				if (current.Length > 0) {
					current = current.Substring(1); // Remove leading dot
				}

				if (!string.IsNullOrEmpty(ext) 
						&& Path.GetExtension(options.locationPathName).EqualsIgnoringCase(current)) {
					options.locationPathName += "." + ext;
				}
			}
		}

		// Run the build
		var error = BuildPipeline.BuildPlayer(options);

		BuildManager.CurrentProfile = null;
		return error;
	}

	// -------- Profiles --------

	/// <summary>
	/// Create and configure the <see cref="ProfileContainer"/> during the build.
	/// </summary>
	private static void InjectProfileContainer(ValueStore store)
	{
		var go = new GameObject("Workbench");
		var container = go.AddComponent<ProfileContainer>();
		container.store = store;
	}

	/// <summary>
	/// Create or udpate the main runtime profile with the apropriate value store.
	/// </summary>
	private static void CreateOrUpdateMainRuntimeProfile()
	{
		if (!Application.isPlaying) {
			Debug.LogError("Cannot create main runtime profile when not playing.");
			return;
		}

		var store = EditorProfile.SharedInstance.Store;
		if (store != null) {
			store = store.Clone();
		}
		
		RuntimeProfile.CreateMain(store);
		RuntimeProfile.Main.CleanStore();
	}

	/// <summary>
	/// Profile used to call Option callbacks during builds.
	/// </summary>
	/// <remarks>
	/// <see cref="BuildProfile"/> only stores the Option values but doesn't
	/// contain Option instances. During build, this BuildOptionsProfile is 
	/// created to instantiate the necessary Options and then to call the
	/// build callbacks on them.
	/// </remarks>
	private class BuildOptionsProfile : RuntimeProfile
	{
		/// <summary>
		/// Option needs to have one of these capabilities to be 
		/// included in the build options profile.
		/// </summary>
		const OptionCapabilities requiredCapabilities = (
			OptionCapabilities.HasAssociatedFeature
			| OptionCapabilities.CanIncludeOption
			| OptionCapabilities.ConfiguresBuild
		);

		public BuildOptionsProfile(ValueStore store) : base(store) { }

		protected override bool ShouldCreateOption(Type optionType)
		{
			var caps = optionType.GetOptionCapabilities();
			return ((caps & requiredCapabilities) != 0);
		}
	}

	static BuildOptionsProfile buildOptionsProfile;

	/// <summary>
	/// Create the build options profile when necessary and
	/// assign it the current store.
	/// </summary>
	private static void CreateOrUpdateBuildOptionsProfile()
	{
		ValueStore store = null;
		if (CurrentProfile != null) {
			store = CurrentProfile.Store;
		}

		if (store != null) {
			store = store.Clone();
		}

		if (buildOptionsProfile == null) {
			buildOptionsProfile = new BuildOptionsProfile(store);
		} else {
			buildOptionsProfile.Store = store;
		}
	}

	/// <summary>
	/// Convenience method to get the current scripting define symbols as a
	/// hash set (instead of a colon-delimited string).
	/// </summary>
	protected HashSet<string> GetCurrentScriptingDefineSymbols(BuildTargetGroup targetGroup)
	{
		var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(targetGroup).Split(';');
		return new HashSet<string>(defines);
	}

	// ------ Unity Callbacks ------

	public int callbackOrder { get { return 0; } }

	public void OnPreprocessBuild(BuildTarget target, string path)
	{
		// Warn if no profile is set
		var buildProfile = CurrentProfile;
		if (buildProfile == null) {
			Debug.LogError("Build Configuration: No current or default profile set, all options removed.");
		}

		// Run options' PreprocessBuild and collect scripting define symbols
		var targetGroup = BuildPipeline.GetBuildTargetGroup(target);
		var symbols = GetCurrentScriptingDefineSymbols(targetGroup);

		// Remove all symbols previously added by Workbench
		symbols.RemoveWhere(d => d.StartsWith(Option.DEFINE_PREFIX));
		var current = new HashSet<string>(symbols);
		
		CreateOrUpdateBuildOptionsProfile();
		foreach (var option in buildOptionsProfile.OrderBy(o => o.PostprocessOrder)) {
			var inclusion = buildProfile == null ? OptionInclusion.Remove : buildProfile.GetInclusionOf(option);

			option.GetSctiptingDefineSymbols(inclusion, symbols);

			if ((option.Capabilities & OptionCapabilities.ConfiguresBuild) != 0) {
				option.PreprocessBuild(target, path, inclusion);
			}
		}

		// Apply scripting define symbols
		PlayerSettings.SetScriptingDefineSymbolsForGroup(targetGroup, string.Join(";", symbols.ToArray()));

		var added = symbols.Except(current);
		var removed = current.Except(symbols);
		Debug.Log(string.Format(
			"Workbench: Building '{0}' to '{1}'\nIncluded: {2}\nSymbols: {3}",
			target, path, 
			buildOptionsProfile
				.Where(o => CurrentProfile.GetInclusionOf(o) != OptionInclusion.Remove)
				.Select(o => o.Name)
				.Join(),
			removed.Select(s => "-" + s).Concat(added.Select(s => "+" + s)).Join()
		));
	}
	
	public void OnPostprocessBuild(BuildTarget target, string path)
	{
		var buildProfile = CurrentProfile;

		// Run options' PostprocessBuild		
		CreateOrUpdateBuildOptionsProfile();
		foreach (var option in buildOptionsProfile.OrderBy(o => o.PostprocessOrder)) {
			if ((option.Capabilities & OptionCapabilities.ConfiguresBuild) == 0) continue;
			var inclusion = buildProfile == null ? OptionInclusion.Remove : buildProfile.GetInclusionOf(option);
			option.PostprocessBuild(target, path, inclusion);
		}
	}

	public void OnProcessScene(Scene scene)
	{
		if (!BuildPipeline.isBuildingPlayer) {
			// When playing only inject runtime profile, Options can use Apply()
			if (RuntimeProfile.Main == null) {
				CreateOrUpdateMainRuntimeProfile();
				RuntimeProfile.Main.Apply();
			}

		} else {
			// Inject profile and call PostprocessScene, Apply() isn't called during build
			var buildProfile = CurrentProfile;
			CreateOrUpdateBuildOptionsProfile();
			
			var includesAnyOption = false;
			foreach (var option in buildOptionsProfile.OrderBy(o => o.PostprocessOrder)) {
				var inclusion = buildProfile == null ? OptionInclusion.Remove : buildProfile.GetInclusionOf(option);
				includesAnyOption |= (inclusion & OptionInclusion.Option) != 0;

				if ((option.Capabilities & OptionCapabilities.ConfiguresBuild) != 0) {
					option.PostprocessScene(scene, inclusion);
				}
			}

			if (includesAnyOption) {
				InjectProfileContainer(buildOptionsProfile.Store);
			}
		}
	}
}

}