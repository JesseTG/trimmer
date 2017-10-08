﻿#if OPTION_Prompt || UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEngine;

namespace sttz.Workbench.Prompt
{

/// <summary>
/// Simple one-line interface to edit options in the player.
/// </summary>
public class Prompt : MonoBehaviour
{
	// ------ Configuration ------

	/// <summary>
	/// Sequence of keys that need to be pressed to activate prompt.
	/// </summary>
	public string activationSequence;

	/// <summary>
	/// Position of the prompt on the screen.
	/// </summary>
	public enum Position
	{
		TopLeft,
		BottomLeft,
		TopRight,
		BottomRight
	}

	/// <summary>
	/// Positio of the prompt on the screen.
	/// </summary>
	public Position position = Position.BottomRight;
	/// <summary>
	/// Padding applied to the edge of the window/screen.
	/// </summary>
	public float padding = 30;
	/// <summary>
	/// Prefix of the prompt.
	/// </summary>
	public string promptPrefix = "> ";
	/// <summary>
	/// Font size used for the prompt (0 = Unity default).
	/// </summary>
	public int fontSize = 0;

	// ------ Prompt ------

	public static Prompt Instance { get; protected set; }

	protected int sequencePos;
	protected bool enablePrompt;

	protected string input = "";
	protected Rect promptRect;

	protected List<string> completions = new List<string>();
	protected int completionIndex;

	protected GUIStyle promptStyle;
	protected float promptHeight;

	protected KeyCode[] keySequence;

	void OnEnable()
	{
		if (Instance != null) {
			Debug.LogWarning("Multiple Prompt instances found.");
			Destroy(this);
			return;
		}

		DontDestroyOnLoad(gameObject);
		Instance = this;
	}

	protected void ParseActivationSequence()
	{
		var parts = activationSequence.Split('-');
		keySequence = new KeyCode[parts.Length];
		for (int i = 0; i < parts.Length; i++) {
			keySequence[i] = (KeyCode)Enum.Parse(typeof(KeyCode), parts[i]);
		}
	}

	protected void Update()
	{
		if (!enablePrompt) {
			if (keySequence == null) {
				ParseActivationSequence();
			}

			// Track activation sequence
			if (UnityEngine.Input.anyKeyDown) {
				/*if (config.activationShift
				    && !UnityEngine.Input.GetKey(KeyCode.LeftShift)
				    && !UnityEngine.Input.GetKey(KeyCode.RightShift)) {
					sequencePos = 0;
					return;
				} else if (config.activationAlt
					&& !UnityEngine.Input.GetKey(KeyCode.LeftAlt)
					&& !UnityEngine.Input.GetKey(KeyCode.RightAlt)) {
					sequencePos = 0;
					return;
				} else if (config.activationCtrlCmd
					&& !UnityEngine.Input.GetKey(KeyCode.LeftControl)
					&& !UnityEngine.Input.GetKey(KeyCode.RightControl)
					&& !UnityEngine.Input.GetKey(KeyCode.LeftCommand)
					&& !UnityEngine.Input.GetKey(KeyCode.RightCommand)) {
					sequencePos = 0;
					return;
				}*/

				if (UnityEngine.Input.GetKeyDown(keySequence[sequencePos])) {
					sequencePos++;
					if (sequencePos == keySequence.Length) {
						StartPrompt();
						sequencePos = 0;
					}
				} else {
					sequencePos = 0;
				}
			}

		} else {
			// Don't process input if a modifier key is held down
			// .e.g. when using cmd-P in editor to stop playback
			if (UnityEngine.Input.GetKey(KeyCode.LeftCommand)
				|| UnityEngine.Input.GetKey(KeyCode.RightCommand)
				|| UnityEngine.Input.GetKey(KeyCode.LeftControl)
				|| UnityEngine.Input.GetKey(KeyCode.RightControl)) {
				return;
			}

			// Handle function keys
			if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) {
				StopPrompt();
				return;
			} else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) {
				CompletePrompt(1);
				return;
			} else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) {
				CompletePrompt(-1);
				return;
			} else if (UnityEngine.Input.GetKeyDown(KeyCode.Tab)) {
				CompletePrompt();
				return;
			}

			if (UnityEngine.Input.inputString.Length == 0)
				return;

			// If anything is added or deleted, completions are cleared
			completions.Clear();

			foreach (var c in UnityEngine.Input.inputString) {
				if (c == '\b') {
					if (input.Length > 0)
						input = input.Remove(input.Length - 1, 1);
				} else if (c == '\n' || c == '\r') {
					ExecutePrompt();
				} else {
					input += c;
				}
			}
		}
	}

	protected void OnGUI()
	{
		if (promptStyle == null) {
			promptStyle = new GUIStyle(GUI.skin.label);
			promptStyle.fontSize = fontSize;
			promptHeight = promptStyle.CalcSize(new GUIContent(promptPrefix)).y;
		}

		if (!enablePrompt)
			return;

		GUI.Label(promptRect, promptPrefix + input, promptStyle);
	}

	protected void StartPrompt()
	{
		enablePrompt = true;

		promptRect = new Rect();
		if (position == Position.TopLeft || position == Position.BottomLeft) {
			promptRect.x = padding;
			promptRect.width = Screen.width - 2 * padding;
		} else {
			promptRect.x = Screen.width / 2;
			promptRect.width = Screen.width / 2 - padding;
		}
		if (position == Position.TopLeft || position == Position.TopRight) {
			promptRect.y = padding;
			promptRect.height = Screen.height - 2 * padding;
		} else {
			promptRect.y = Screen.height - padding - promptHeight;
			promptRect.height = promptHeight;
		}
	}

	protected void StopPrompt()
	{
		enablePrompt = false;
	}

	protected void CompletePrompt(int moveIndex = 0)
	{
		// Generate completions list
		if (completions.Count == 0 || moveIndex == 0) {
			completions.Clear();

			var path = IniAdapter.NameToPath(input);
			if (path != null) {
				foreach (var option in RuntimeProfile.Main) {
					#if UNITY_EDITOR
					if (option.BuildOnly)
						continue;
					#endif

					// TODO: Fix completion not working properly
					if (option.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase)) {
						CompleteOptionRecursive(path, option, "");
					}
				}
			}
			if (completions.Count > 0) {
				completions.Sort();
				completions.Insert(0, input);
				completions.Add("");
				completionIndex = 1;
			}
		
		// Move complections index
		} else {
			completionIndex = Mathf.Max(Mathf.Min(completionIndex + moveIndex, completions.Count - 1), 0);
		}

		// Show new completion
		if (completionIndex < completions.Count) {
			input = completions[completionIndex];
		}
	}

	protected void CompleteOptionRecursive(string path, IOption option, string baseInput)
	{
		if (!option.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase))
			return;

		if (option.Parent == null || option.IsDefaultVariant || !option.IsVariant) {
			if (option.Parent != null) {
				baseInput += ".";
			}
			baseInput += option.Name;
		}

		if (option.IsVariant && !option.IsDefaultVariant) {
			baseInput += "[" + option.VariantParameter + "]";
		}
		
		completions.Add(baseInput + " = " + option.Save());

		foreach (var variant in option.Variants) {
			CompleteOptionRecursive(path, variant, baseInput);
		}

		foreach (var child in option.Children) {
			CompleteOptionRecursive(path, child, baseInput);
		}
	}

	protected void ExecutePrompt()
	{
		// Enter on empty prompt closes it
		if (input.Length == 0) {
			StopPrompt();

		// Set a option value
		} else if (input.Contains("=")) {
			var path = IniAdapter.NameToPath(input);
			if (path != null) {
				var option = RuntimeProfile.Main.GetOption(path);
				var value = IniAdapter.GetValue(input);
				if (option != null && value != null) {
					option.Load(value);
					input = "";
				}
			}
		
		// Enter on an option shows it's value
		} else {
			var path = IniAdapter.NameToPath(input);
			if (path != null) {
				var option = RuntimeProfile.Main.GetOption(path);
				if (option != null) {
					input += " = " + option.Save();
				}
			}
		}
	}
}

}

#endif