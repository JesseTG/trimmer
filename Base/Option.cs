﻿// Workaround for docfx documentation building
#if !UNITY_5 && !UNITY_2017 && !UNITY_2018
#define UNITY_EDITOR
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.SceneManagement;
using sttz.Workbench.Extensions;
using System.Diagnostics;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace sttz.Workbench {

// TODO: Document editor-only methods/props
// TODO: Document main-option-only methods/props
// TODO: Inject and DontDestroyOnLoad

#if UNITY_EDITOR

/// <summary>
/// Enum indicating how and option behaves during the build process.
/// </summary>
/// <remarks>
/// Conceptually, Workbench deals with Options that are its building blocks and
/// configure some aspect of a project dynamically. That aspect of the project
/// is referred to as a «feature».
/// 
/// When building, there are a few scenarios, how an option and its feature are
/// compiled:
/// * Both are included: The build includes both the Workbench Option as well
///   as its associated feature. The Option allows to configure the feature at
///   runtime.
/// * Only the feature is included: The Option only configures the feature in
///   the editor. At build-time the Option statically configures the feature
///   in the build and is itself not included. The feature cannot be configured
///   at runtime using Workbench.
/// * Both are removed: Neither the Option nor its feature are included in the
///   build and if set up correctly, the build won't contain a trace that the
///   Option or feature ever existed.
/// 
/// As an example, assume there's a platform integration that requires an API key.
/// The feature is the integration script and maybe some conditionally-compiled
/// snippets of code in other scripts. The Option controls the conditional compilation,
/// injects the integration script when enabled and configures the API key.
/// 
/// When building for another platform, the integration should be completely 
/// removed, leaving no unrelated code in the build. When doing a release build,
/// the API key should be baked into the build, the Option removed and no way
/// left to change the API key at runtime. In a development build, the Option
/// might be included, to be able to override the API key to one used for testing.
/// 
/// In this scenario, different build profiles would configure the build differently:
/// Profiles for other platforms would completely remove the build and feature,
/// the release build profile would only remove the Option and the development
/// profile would include both Option and feature.
/// </remarks>
[Flags]
public enum OptionInclusion
{
	/// <summary>
	/// Remove the feature and the option form the build.
	/// </summary>
	Remove = 0,

	/// <summary>
	/// Flag indicating the feature should be included.
	/// </summary>
	Feature = 1<<0,

	/// <summary>
	/// Flag indicating the option should be included.
	/// </summary>
	Option = 1<<1,

	/// <summary>
	/// Mask including both feature and option.
	/// </summary>
	FeatureAndOption = Feature | Option
}

/// <summary>
/// Helper class with extensions to check <see cref="OptionInclusion"/> flags.
/// </summary>
public static class OptionInclusionExtensions
{
	/// <summary>
	/// Check wether the <see cref="OptionInclusion"/> mask includes the <see cref="OptionInclusion.Option"/> flag.
	/// </summary>
	public static bool IncludesOption(this OptionInclusion inclusion)
	{
		return (inclusion & OptionInclusion.Option) == OptionInclusion.Option;
	}

	/// <summary>
	/// Check wether the <see cref="OptionInclusion"/> mask includes the <see cref="OptionInclusion.Feature"/> flag.
	/// </summary>
	public static bool IncludesFeature(this OptionInclusion inclusion)
	{
		return (inclusion & OptionInclusion.Feature) == OptionInclusion.Feature;
	}
}

#endif

/// <summary>
/// Enum indicating the capabilities of the Option.
/// </summary>
/// <remarks>
/// The enum contains specific flags that represent different capabilities and also
/// a set of default masks that represent common combinations of flags.
/// 
/// The capabilities control where an Option is visible:
/// * If neither <see cref="HasAssociatedFeature"/>, <see cref="CanIncludeOption"/>
///   or <see cref="ConfiguresBuild"/> is set, the Option will not be shown in
///   Build Profiles.
/// * If neither <see cref="CanPlayInEditor"/> or <see cref="ExecuteInEditMode"/> is set,
///   the Option will not be shown in the Editor Profile.
/// 
/// Capabilities are only valid on the main Option, all child and variant Options will
/// inherit the capabilities from the main Option.
/// </remarks>
[Flags]
public enum OptionCapabilities
{
	None,

	// ------ Flags ------

	/// <summary>
	/// Flag indicating the option has an associated feature that can be included/excluded from the
	/// build using Build Profiles.
	/// </summary>
	HasAssociatedFeature = 1<<0,
	
	/// <summary>
	/// Flag indicating the Option can be included in builds. If not set, the Option will always
	/// be removed from builds.
	/// </summary>
	CanIncludeOption = 1<<1,

	/// <summary>
	/// Flag indicating the Option integrates into the build process, configuring the build
	/// options, setting conditional compilation symbols or pre-/post-processes scenes and
	/// the build.
	/// </summary>
	ConfiguresBuild = 1<<2,

	/// <summary>
	/// Flag indicating the Option can be used when playing in the editor. If not set, the Option
	/// will not be loaded when playing the project in the editor.
	/// </summary>
	CanPlayInEditor = 1<<3,

	/// <summary>
	/// Flag indicating the Option should be loaded in edit mode as well. If set, the Option
	/// will be loaded when not playing in the editor.
	/// </summary>
	ExecuteInEditMode = 1<<4,

	// ------ Presets ------

	/// <summary>
	/// Default preset mask. The Option can be included in the build, its 
	/// build configuration callbacks are called and it is loaded when playing
	/// in the editor.
	/// </summary>
	PresetDefault = CanIncludeOption | ConfiguresBuild | CanPlayInEditor,

	/// <summary>
	/// Default preset mask. Like <see cref="PresetDefault"/> but also has an associated 
	/// feature that can be included/excluded from the build.
	/// </summary>
	PresetWithFeature = PresetDefault | HasAssociatedFeature,

	/// <summary>
	/// Default preset mask. A simple Option that can be included in the build
	/// and gets loaded in the editor but doesn't process the build and has 
	/// no assocaited feature.
	/// </summary>
	OptionOnly = CanIncludeOption | CanPlayInEditor,
}

/// <summary>
/// Attribute used to indicate the <see cref="OptionCapabilities" /> of an 
/// <see cref="Option"/> subclass.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
[Conditional("UNITY_EDITOR")]
public class CapabilitiesAttribute : Attribute
{
	public OptionCapabilities Capabilities { get; protected set; }

	public CapabilitiesAttribute(OptionCapabilities caps)
	{
		Capabilities = caps;
	}
}

/// <summary>
/// Define how an Option can be variant.
/// </summary>
public enum OptionVariance
{
	/// <summary>
	/// The Option is not variant. There exists only a single instance with a single value.
	/// </summary>
	Single,
	
	/// <summary>
	/// The Option is a dictionary. It has variants that differ by their parameter
	/// and the parameter is set explicitly.
	/// </summary>
	Dictionary,

	/// <summary>
	/// The Option is an array. It has variants that are ordered by an index and
	/// the parameter is automatically set.
	/// </summary>
	Array
}

/// <summary>
/// Base class for Workebnch Options.
/// </summary>
/// <remarks>
/// Options are the basic building blocks to integrate your project 
/// into Workbench. Workbench detects all <see cref="Option"/> classes
/// in your project, so there's no additional configuration necessary
/// besides adding the Option source files to your project.
/// 
/// Each Option has a value, which you can edit in the editor and which
/// can also be changed in the player using <see cref="RuntimeProfile"/>.
/// The runtime profile is only a script API, use the bundled Options to
/// change Option values in the player using configuration files 
/// (<see cref="Options.OptionIniFile"/>) or using a simple GUI 
/// (<see cref="Options.OptionPrompt"/>).
/// 
/// Options can model more complicated data than simple values in two ways:
/// * <b>Variant Options</b> allow to have multiple instances of the same
///   Option type that differ by their <see cref="Option.VariantParameter"/>,
///   e.g. to have a volume Option, which can control multiple channels.
/// * <b>Child Options</b> allow Options to group multiple different values
///   together.
/// 
/// Child and variant Options can be nested, with the only limitation that
/// variant Options cannot be directly nested (but a variant option can
/// have a variant child option).
/// 
/// Most of the time, you want to extend one of the typed base classes
/// that fit the type of Option you want to create:
/// * <see cref="BaseOptions.OptionAsset{TUnity}" />
/// * <see cref="BaseOptions.OptionEnum{TEnum}" />
/// * <see cref="BaseOptions.OptionFloat" />
/// * <see cref="BaseOptions.OptionInt" />
/// * <see cref="BaseOptions.OptionString" />
/// * <see cref="BaseOptions.OptionToggle" />
/// 
/// 
/// </remarks>
public abstract class Option
{
	// -------- Implement / Override in Sub-Classes --------

	/// <summary>
	/// Configure the Option instance during instantiation.
	/// </summary>
	/// <remarks>
	/// Override this method instead of the contrustor to configure your
	/// Option instance. Most Option properties should only bet set once
	/// in this method and then not changed after the Option is created.
	/// </remarks>
	protected virtual void Configure()
	{
		// NOP
	}

	/// <summary>
	/// Prefix for the per-option scripting defines.
	/// </summary>
	/// <remarks>
	/// TODO
	/// </remarks>
	public const string DEFINE_PREFIX = "WB_";

	/// <summary>
	/// Prefix applied after the <see cref="DEFINE_PREFIX"/> for the 
	/// Option scripting define.
	/// </summary>
	public const string OPTION_PREFIX = "Option";

	#if UNITY_EDITOR

	/// <summary>
	/// The capabilities of the Option (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// Used to cache the attribute value. To change the capabilities,
	/// use the <see cref="CapabilitiesAttribute"/>.
	/// </remarks>
	public OptionCapabilities Capabilities { get; private set; }

	/// <summary>
	/// The `BuildTarget`s this Option supports. (null = all)
	/// </summary>
	public IEnumerable<BuildTarget> SupportedTargets { get; protected set; }

	/// <summary>
	/// Determine if the Option is available on the given build targets (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// It's possible to hide an Option in Build Profiles if they don't
	/// apply to the profile's build targets (i.e. an iOS-only option on
	/// a Android Build Profile). Unavailable Options can be shown using
	/// an Option in the Workbench's preferences but they will always
	/// be removed from builds.
	/// 
	/// **This method only applies to main Options. The availability of
	/// child and variant Options is inherited from their main Option.**
	/// </remarks>
	public virtual bool IsAvailable(IEnumerable<BuildTarget> targets)
	{
		if (SupportedTargets == null) {
			return true;
		} else {
			foreach (var target in targets) {
				if (SupportedTargets.Contains(target))
					return true;
			}
			return false;
		}
	}

	/// <summary>
	/// This method is only called if the Option has an associated 
	/// feature and only the feature is included in the build.
	/// 
	/// The method allows the Option to check if the feature is
	/// properly configured and only include it if it is. Since
	/// only the feature is included and the option is not, it's
	/// potentially not possible to properly configure the feature
	/// in the build and therefore it makes no sense to include it.
	/// 
	/// Returning `false` will change the inclusion from 
	/// <see cref="OptionInclusion.Feature"/> to <see cref="OptionInclusion.Remove"/>.
	/// </summary>
	public virtual bool ShouldIncludeOnlyFeature()
	{
		return true;
	}

	/// <summary>
	/// Callback invoked for every scene during build or when a scene is played in the editor (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// This callback gives Options a chance to modify scenes during the
	/// build process or when playing in the editor. This can be used
	/// to e.g. inject a script into the scene or remove some game objects.
	/// </remarks>
	/// <param name="scene">The scene that is being processed.</param>
	/// <param name="inclusion">Wether the option is included in the build.</param>
	public virtual void PostprocessScene(Scene scene, OptionInclusion inclusion)
	{
		if (variants != null) {
			foreach (var variant in variants) {
				variant.PostprocessScene(scene, inclusion);
			}
		}

		if (children != null) {
			foreach (var child in children) {
				child.PostprocessScene(scene, inclusion);
			}
		}
	}

	/// <summary>
	/// The priority of the Option's processing callbacks (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// This determines the order in which all Option's processing callbacks
	/// are called (<see cref="PostprocessScene"/>, <see cref="PrepareBuild"/>
	/// and <see cref="PostprocessBuild"/>).
	/// 
	/// Lower values will be called first.
	/// 
	/// Note that this only orders the Options between themselves. This does
	/// not affect the order in regard to other consumers of these Unity events.
	/// 
	/// **The order only applies to main Options and will be ignored on variant
	/// or child options.**
	/// </remarks>
	public int PostprocessOrder { get; protected set; }

	/// <summary>
	/// Callback invoked before a profile build is started (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// When a build is started on a <see cref="Editor.BuildProfile"/>, all options
	/// will receive this callback before the build is started.
	/// 
	/// This callback allows Option to influence the build settings, including
	/// build options, output path and included scenes.
	/// 
	/// By default, the build will include the scenes set in Unity's build 
	/// player window and the options will be set to `BuildOptions.None`.
	/// If no Option set the location path name, the user will be prompted 
	/// to choose it.
	/// 
	/// Noe that this method will not be called for regular Unity builds,
	/// started from the build player menu or using the build menu item.
	/// </remarks>
	/// <param name="options">The current options</param>
	/// <param name="inclusion">Wether the Option is included in the  build.</param>
	/// <returns>The modified options.</returns>
	public virtual BuildPlayerOptions PrepareBuild(BuildPlayerOptions options, OptionInclusion inclusion)
	{
		if (variants != null) {
			foreach (var variant in variants) {
				options = variant.PrepareBuild(options, inclusion);
			}
		}

		if (children != null) {
			foreach (var child in children) {
				options = child.PrepareBuild(options, inclusion);
			}
		}

		return options;
	}

	/// <summary>
	/// Callback invoked right before a build (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// This callback is invoked before the build for both profile builds
	/// as well as regular Unity builds.
	/// 
	/// Build settings have already been determined at this stage and cannot
	/// be changed.
	/// </remarks>
	/// <param name="target">Build target type</param>
	/// <param name="path">Path to the built project</param>
	/// <param name="inclusion">Wether this option is included in the build</param>
	public virtual void PreprocessBuild(BuildTarget target, string path, OptionInclusion inclusion)
	{
		if (variants != null) {
			foreach (var variant in variants) {
				variant.PreprocessBuild(target, path, inclusion);
			}
		}

		if (children != null) {
			foreach (var child in children) {
				child.PreprocessBuild(target, path, inclusion);
			}
		}
	}

	/// <summary>
	/// Callback invoked when the build completed (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// This callback is invoked after the build has been completed for 
	/// both profile builds and regular Unity builds.
	/// </remarks>
	/// <param name="target">Build target type</param>
	/// <param name="path">Path to the built project</param>
	/// <param name="inclusion">Wether this option is included in the build</param>
	public virtual void PostprocessBuild(BuildTarget target, string path, OptionInclusion inclusion)
	{
		if (variants != null) {
			foreach (var variant in variants) {
				variant.PostprocessBuild(target, path, inclusion);
			}
		}

		if (children != null) {
			foreach (var child in children) {
				child.PostprocessBuild(target, path, inclusion);
			}
		}
	}

	/// <summary>
	/// The scripting define symbols set by this option (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// By default, this method returns `<see cref="DEFINE_PREFIX"/> + <see cref="Name"/>`
	/// for main Options that are included in the build and nothing for child or variant
	/// options or excluded options.
	/// </remarks>
	public virtual void GetSctiptingDefineSymbols(OptionInclusion inclusion, HashSet<string> symbols)
	{
		// Only the root option has a toggle in the build profile
		if (Parent != null || inclusion == OptionInclusion.Remove)
			return;

		if ((inclusion & OptionInclusion.Feature) != 0 
				&& (Capabilities & OptionCapabilities.HasAssociatedFeature) != 0) {
			symbols.Add(DEFINE_PREFIX + Name);
		}

		if ((inclusion & OptionInclusion.Option) != 0 
				&& (Capabilities & OptionCapabilities.CanIncludeOption) != 0) {
			symbols.Add(DEFINE_PREFIX + OPTION_PREFIX + Name);
		}
	}

	/// <summary>
	/// Do the editor GUI to edit this option (**Editor-only**).
	/// </summary>
	/// <remarks>
	/// The bundled subclasses in <see cref="sttz.Workbench.BaseOptions"/>
	/// already provide implementations for this method. Override it
	/// to implement your custom GUI for the editor.
	/// 
	/// Note that this method is called on a shared and not fully initialized
	/// Option instance. Most notably, the Option's value is not set and a 
	/// single instance will be used to edit all variants of an Option.
	/// </remarks>
	public abstract string EditGUI(string input);

	#endif

	/// <summary>
	/// The name of the option.
	/// </summary>
	/// <remarks>
	/// The name is used in the editor, to identify the option in config
	/// files and to set the Option's scripting define symbol.
	/// 
	/// By default, the name is the Option's class name, minus the 
	/// <see cref="DEFINE_PREFIX"/>. This ensures that the Option's class name
	/// and the scripting define symbol name match.
	/// 
	/// In case you set the name to sometehing that doesn't start with the 
	/// define prefix, the prefix wil be prepended to the Option's scripting
	/// define symbol.
	/// 
	/// e.g.
	/// Class Name &#x2192; Option Name &#x2192; Scripting Define Symbol<br/>
	/// OptionExample &#x2192; Example &#x2192; OptionExample<br/>
	/// NonDefaultExample &#x2192; NonDefaultExample &#x2192; OptionNonDefaultExample
	/// </remarks>
	public string Name { get; protected set; }

	/// <summary>
	/// The Option containing this option (if any).
	/// </summary>
	/// <remarks>
	/// In case of variant Options, the parent is set to the main variant
	/// that contains all other variants.
	/// 
	/// In case of child Options and the main variant, the parent is set 
	/// to the Option containing the child / main variant.
	/// 
	/// The parent is `null` for main Options.
	/// </remarks>
	public Option Parent {
		get {
			return _parent;
		}
		set {
			if (_parent == value) return;

			_parent = value;
			
			InvalidatePathRecursive();
		}
	}
	private Option _parent;

	/// <summary>
	/// The path to this Option.
	/// </summary>
	/// <summary>
	/// The path consists of option names separated by «/» and variants 
	/// separated by «:» and their parameter.
	/// 
	/// You can use <see cref="RuntimeProfile.GetOption"/> to find an
	/// Option by its path.
	/// </summary>
	public string Path {
		get {
			if (_path == null) {
				_path = GetPathRecursive(this);
			}
			return _path;
		}
	}
	protected string _path;

	/// <summary>
	/// Internal helper method to construct an Option's path recursively.
	/// </summary>
	protected string GetPathRecursive(Option current)
	{
		if (current.Variance != OptionVariance.Single && !current.IsDefaultVariant) {
			if (current.Parent != null) {
				return GetPathRecursive(current.Parent) + ":" + current.VariantParameter;
			} else {
				throw new Exception("A non-default variant needs to have a parent.");
			}
		} else {
			if (current.Parent != null) {
				return GetPathRecursive(current.Parent) + "/" + current.Name;
			} else {
				return current.Name;
			}
		}
	}

	/// <summary>
	/// Internal helper method to invalidate all child/variant Option's
	/// path recursively.
	/// </summary>
	public void InvalidatePathRecursive()
	{
		_path = null;

		foreach (var child in Children) {
			child.InvalidatePathRecursive();
		}
		foreach (var variant in Variants) {
			variant.InvalidatePathRecursive();
		}
	}

	/// <summary>
	/// Parse and load an input string.
	/// </summary>
	/// <remarks>
	/// This method is called with the value defined in the profile
	/// or entered by the user. This method should parse the input
	/// and then save it to <see cref="Option{TValue}.Value"/>.
	/// If the input is empty or contains an invalid value, the 
	/// <see cref="DefaultValue"/> should be used instead.
	/// </remarks>
	public abstract void Load(string input);
	/// <summary>
	/// Serialize the option's value to a string.
	/// </summary>
	/// <remarks>
	/// The value returned by this method will later be supplied to
	/// <see cref="Load"/> and should survive the round-trip without
	/// loss.
	/// </remarks>
	public abstract string Save();

	/// <summary>
	/// Control the order Options' <see cref="Apply"/> method get called.
	/// </summary>
	/// <remarks>
	/// Lower values get called first.
	/// </remarks>
	public int ApplyOrder { get; protected set; }

	/// <summary>
	/// Apply the option to the current environment.
	/// </summary>
	/// <remarks>
	/// This method is called when the Option should act on its value.
	/// 
	/// This is when the game is started in the editor or in a player,
	/// or when the Option's value is changed while the game is playing.
	/// 
	/// Main Options as well as its children and variants are applied 
	/// together. E.g. when the main Option's or one of its children's
	/// value is changed, all of their Apply methods will be called.
	/// 
	/// This method does not get called when scenes change. Use Unity's
	/// `SceneManager` callbacks to get notified when scenes get loaded
	/// and unloaded or the active scene changes.
	/// </remarks>
	public virtual void Apply()
	{
		if (variants != null) {
			foreach (var variant in variants) {
				variant.Apply();
			}
		}

		if (children != null) {
			foreach (var child in children) {
				child.Apply();
			}
		}
	}

	/// <summary>
	/// Look for the root option and then call its Apply method.
	/// </summary>
	public void ApplyFromRoot()
	{
		Option root = this;
		while (root.Parent != null) {
			root = root.Parent;
		}

		root.Apply();
	}

	// -------- Init --------

	/// <summary>
	/// Main constructor.
	/// </summary>
	/// <remarks>
	/// All Option classes need to have a constructor with no arguments.
	/// 
	/// **Don't override this constructor to initialize your Option
	/// subclass. Override <see cref="Configure"/> instead.**
	/// </remarks>
	public Option()
	{
		Name = GetType().Name;
		if (Name.StartsWith(OPTION_PREFIX)) {
			Name = Name.Substring(OPTION_PREFIX.Length);
		}

		Parent = null;

		#if UNITY_EDITOR
		Capabilities = OptionCapabilities.PresetDefault;
		var attr = (CapabilitiesAttribute)GetType()
			.GetCustomAttributes(typeof(CapabilitiesAttribute), true)
			.FirstOrDefault();
		if (attr != null) {
			Capabilities = attr.Capabilities;
		}
		#endif
		
		Configure();
		
		if (Variance != OptionVariance.Single) {
			IsDefaultVariant = true;
			VariantParameter = VariantDefaultParameter;
			if (string.IsNullOrEmpty(VariantDefaultParameter)) {
				if (Variance == OptionVariance.Array) {
					VariantDefaultParameter = "0";
				} else {
					VariantDefaultParameter = "Default";
				}
			}
		}

		CreateChildren();
	}

	// -------- Variants --------

	/// <summary>
	/// The variance of the Option.
	/// </summary>
	/// <returns>
	/// By default an Option is invariant and there's only a single instance / value of it.
	/// 
	/// An Option can also be variant, in which case multiple instances can exist and
	/// the instances are distinguished by their parameter.
	/// 
	/// There are two types of variance, where the only difference is if their parameters
	/// are set by the user (<see cref="OptionVariance.Dictionary"/>) or if an index
	/// is assigned automatically as parameter (<see cref="OptionVariance.Array"/>).
	/// 
	/// Variant Options have a default variant (<see cref="IsDefaultVariant"/>), that acts 
	/// as the container for all other variants and its parameter is always set to 
	/// the <see cref="VariantDefaultParameter"/>.
	/// </returns>
	public OptionVariance Variance { get; protected set; }

	/// <summary>
	/// The parameter of a variant Option.
	/// </summary>
	/// <remarks>
	/// The parameter is only used when <see cref="Variance"/> is not 
	/// <see cref="OptionVariance.Single"/>.
	/// 
	/// When <see cref="Variance"/> is <see cref="OptionVariance.Array"/>, the
	/// parameter is assigned automatically and will be overwritten if it's changed.
	/// 
	/// To change a parameter, it's best to remove the variant, change the parameter
	/// and then add it again. This detects duplicate parameters, which can cause
	/// undefined behavior.
	/// </remarks>
	public string VariantParameter { get; set; }
	/// <summary>
	/// The parameter of the default variant.
	/// </summary>
	/// <remarks>
	/// Variants are created on-demand when a new parameter appears but there
	/// is always a default variant, which is assigned the default parameter.
	/// </remarks>
	public string VariantDefaultParameter { get; protected set; }
	/// <summary>
	/// Wether this option instance is the default variant.
	/// </summary>
	/// <remarks>
	/// Variant options can have an arbitrary number of instances, each with
	/// a different variant parameter to distinguish them. Variant options are
	/// created on-demand when a new paramter appears. However, the one
	/// instance using the <see cref="VariantDefaultParameter"/> is guaranteed 
	/// to always exist and acts as container for the other variants.
	/// 
	/// <see cref="AddVariant"/>, <see cref="GetVariant"/> and <see cref="RemoveVariant"/>
	/// can only be called on the default variants.
	/// </remarks>
	public bool IsDefaultVariant { get; set; }

	private List<Option> variants;

	/// <summary>
	/// The variants contained in the default variant.
	/// </summary>
	/// <remarks>
	/// Only the <see cref="IsDefaultVariant" /> Option can contain variants.
	/// All other Options return an empty enumerable.
	/// 
	/// The enumerable does not contain the default variant itself.
	/// </remarks>
	public IEnumerable<Option> Variants {
		get {
			if (variants == null) {
				return Enumerable.Empty<Option>();
			} else {
				return variants;
			}
		}
	}

	/// <summary>
	/// Add a new variant option.
	/// </summary>
	/// <remarks>
	/// Variants can only be added to the <see cref="IsDefaultVariant" /> instance
	/// of an Option whose <see cref="Variance" /> is not <see cref="OptionVariance.Single" />.
	/// 
	/// The default variant acts as a container for all other variants.
	/// </remarks>
	public Option AddVariant(string parameter)
	{
		Assert.IsTrue(Variance != OptionVariance.Single, "Invalid call to AddVariant, option is not variant.");
		Assert.IsTrue(IsDefaultVariant, "Invalid call to AddVariant, option is not the default variant.");

		Assert.IsNotNull(parameter);
		Assert.IsFalse(string.Equals(parameter, VariantDefaultParameter, StringComparison.OrdinalIgnoreCase), "Cannot add variant with default parameter.");
		Assert.IsTrue(variants == null || variants.Find(v => v.VariantParameter.EqualsIgnoringCase(parameter)) == null, "Variant with paramter already exists.");

		var instance = (Option)Activator.CreateInstance(GetType());
		instance.Parent = this;
		instance.VariantParameter = parameter;
		instance.IsDefaultVariant = false;

		if (variants == null)
			variants = new List<Option>();
		variants.Add(instance);

		if (Variance == OptionVariance.Array) {
			RenumberArrayVariants();
		}

		return instance;
	}

	/// <summary>
	/// Get the variant Option for the given parameter.
	/// </summary>
	/// <remarks>
	/// Variants only exist on the <see cref="IsDefaultVariant" /> instance
	/// of an Option whose <see cref="Variance" /> is not <see cref="OptionVariance.Single" />.
	/// 
	/// `GetVariant` can also be used to get the default variant itself,
	/// i.e. when <paramref name="parameter"/> equals <see cref="VariantDefaultParameter" />,
	/// the method will return `this`.
	/// </remarks>
	/// <param name="create">Wether a new variant should be created if one doesn't currently exist</param>
	public Option GetVariant(string parameter, bool create = true)
	{
		Assert.IsTrue(Variance != OptionVariance.Single, "Invalid call to GetVariant, option is not variant.");
		Assert.IsTrue(IsDefaultVariant, "Invalid call to GetVariant, option is not the default variant.");

		if (string.Equals(parameter, VariantDefaultParameter, StringComparison.OrdinalIgnoreCase))
			return this;

		if (!create && variants == null)
			return null;

		Option variant = null;
		if (variants != null) {
			variant = variants.Find(v => v.VariantParameter.EqualsIgnoringCase(parameter));
		}
		
		if (create && variant == null) {
			variant = AddVariant(parameter);
		}

		return variant;
	}

	/// <summary>
	/// Remove a variant Option.
	/// </summary>
	/// <remarks>
	/// Variants only exist on the <see cref="IsDefaultVariant" /> instance
	/// of an Option whose <see cref="Variance" /> is not <see cref="OptionVariance.Single" />.
	/// </remarks>
	public void RemoveVariant(Option option)
	{
		Assert.IsTrue(Variance != OptionVariance.Single, "Invalid call to RemoveVariant, option is not variant.");
		Assert.IsTrue(IsDefaultVariant, "Invalid call to RemoveVariant, option is not the default variant.");

		Assert.IsTrue(variants != null && variants.Contains(option), "Invalid call to RemoveVariant, option is not a variant of this instance.");

		variants.Remove(option);
		option.Parent = null;

		if (Variance == OptionVariance.Array) {
			RenumberArrayVariants();
		}
	}

	/// <summary>
	/// Internal helper method that ensures parameters in array variants are all numbers and sequential.
	/// </summary>
	protected void RenumberArrayVariants()
	{
		Assert.IsTrue(Variance == OptionVariance.Array, "Invalid call to RenumberArrayVariants, option is not an array variant.");
		Assert.IsTrue(IsDefaultVariant, "Invalid call to RenumberArrayVariants, option is not the default variant.");

		// Default variant is always 0
		VariantParameter = "0";

		// First order parameters using natural sort, then assign sequential indices
		var comparer = NumericStringComparer.Instance;
		variants.Sort((a, b) => comparer.Compare(a.VariantParameter, b.VariantParameter));
		for (int i = 0; i < variants.Count; i++) {
			variants[i].VariantParameter = (i + 1).ToString();
		}
	}

	// -------- Children --------

	private List<Option> children;

	/// <summary>
	/// Wether this option has children.
	/// </summary>
	public bool HasChildren {
		get {
			return children != null && children.Count > 0;
		}
	}

	/// <summary>
	/// The children of this option.
	/// </summary>
	/// <remarks>
	/// Child options are nested classes of the current Option class.
	/// They are detected automatically and instantaited when their
	/// parent Option is instantiated.
	/// </remarks>
	public IEnumerable<Option> Children {
		get {
			if (children == null) {
				return Enumerable.Empty<Option>();
			} else {
				return children;
			}
		}
	}

	/// <summary>
	/// Internal helper method to create the child Option instances.
	/// </summary>
	protected void CreateChildren()
	{
		var type = GetType();

		var nested = type.GetNestedTypes(BindingFlags.Public);
		foreach (var nestedType in nested) {
			if (!typeof(Option).IsAssignableFrom(nestedType))
				continue;

			if (children == null)
				children = new List<Option>();

			var child = (Option)Activator.CreateInstance(nestedType);
			child.Parent = this;
			children.Add(child);
		}

		if (children != null) {
			children.Sort((a, b) => a.ApplyOrder.CompareTo(b.ApplyOrder));
		}
	}

	/// <summary>
	/// Get a child Option by its name.
	/// </summary>
	public Option GetChild(string name)
	{
		if (children == null)
			return null;

		foreach (var child in children) {
			if (child.Name.EqualsIgnoringCase(name)) {
				return child;
			}
		}

		return null;
	}

	/// <summary>
	/// Get a child Option instance by its type.
	/// </summary>
	public TOption GetChild<TOption>() where TOption : Option
	{
		if (children != null) {
			foreach (var child in children) {
				if (child is TOption)
					return (TOption)child;
			}
		}

		return default(TOption);
	}

	// -------- Category --------

	/// <summary>
	/// Category of the option, only used in the editor.
	/// </summary>
	/// <remarks>
	/// The category is used to group options in the editor.
	/// Only the main option can have a category, the value is
	/// ignored for child or variant options.
	/// </remarks>
	public string Category {
		get {
			return _category;
		}
		protected set {
			_category = value;
		}
	}
	string _category = "General";
}

public abstract class Option<TValue> : Option
{
	/// <summary>
	/// The typed value of the Option.
	/// </summary>
	public TValue Value { get; set; }

	/// <summary>
	/// The default value, used when input is empty or invalid.
	/// </summary>
	public TValue DefaultValue { get; protected set; }

	/// <summary>
	/// Parse a string value to the Option Value's type.
	/// </summary>
	/// <remarks>
	/// If the input is empty or parsing fails, <see cref="DefaultValue"/>
	/// should be used.
	/// 
	/// The method can be called on not fully initialized and sahred Option 
	/// instances and should be careful when relying on external state.
	/// </remarks>
	public abstract TValue Parse(string input);

	/// <summary>
	/// Serialize a typed value to a string.
	/// </summary>
	/// <remarks>
	/// The string returned by Save can later be fed back to <see cref="Parse" />
	/// and should survive the round-trip without loss.
	/// 
	/// The method can be called on not fully initialized and sahred Option 
	/// instances and should be careful when relying on external state.
	/// </remarks>
	public abstract string Save(TValue input);
}

}

