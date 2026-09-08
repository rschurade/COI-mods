using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Localization;
using Mafi.Unity.InputControl.GameMenu.Settings;
using Mafi.Unity.Ui.GameMenu;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using Mafi.Unity.UiToolkit.Themes;
using VisualElement = UnityEngine.UIElements.VisualElement;

namespace ModConfigPP;

/// <summary>
/// Adds a "Mod settings" entry to the in-game escape menu that edits the LIVE mod configs.
///
/// The vanilla mod-settings button (main menu mods window) is enabled only while a mod is not
/// yet part of the selected save; once it is, there is no UI anywhere to change its settings,
/// even though the values are plain <see cref="ModJsonConfig"/> parameters that are serialized
/// into the save and that mods can react to at runtime.
///
/// This dialog edits the real thing: the game registers every mod's own <c>IMod.JsonConfig</c>
/// instance in the dependency resolver (GameBuilder.AttachModJsonConfigs swaps the mod's live,
/// subscriber-preserving instance into the configs array before RegisterModDependenciesOrThrow),
/// so <c>resolver.ResolveAll&lt;ModJsonConfig&gt;()</c> yields exactly the configs the running
/// game uses. Editing them via the vanilla <see cref="ModJsonConfigPanel"/> shows the actual
/// current values, fires each config's <c>OnValueChanged</c> (mods that subscribe — like
/// Elevation++ — apply changes immediately; mods that only read their config at init pick the
/// values up on the next load), and the values are saved with the game like always.
///
/// UI: a Harmony postfix on the <see cref="GameEscapeMenu"/> constructor appends a "Mod settings"
/// button (placed above the seed/version info block), which opens a window styled like the
/// vanilla main-menu mod config dialog: one section per mod with a non-empty config — bold mod
/// id, the vanilla parameter panel, and a per-mod "Restore defaults" button. Restore re-sets
/// every parameter through <c>TrySetValue</c> because <c>ResetToDefaults</c> alone does not fire
/// <c>OnValueChanged</c>.
/// </summary>
internal static class InGameModSettings
{
    private const string HARMONY_ID = "com.roest.modconfigpp.settings";

    private static readonly LocStr BUTTON_TEXT = Loc.Str("ModConfigPP_ModSettings",
        "Mod settings",
        "button in the in-game escape menu (and title of the window it opens) for editing mod settings");
    private static readonly LocStr NOTE_TEXT = Loc.Str("ModConfigPP_ModSettingsNote",
        "These are the settings the running game uses; changes are stored in this save. "
        + "Mods that support live changes apply them immediately, others use the new "
        + "values after the game is saved and reloaded.",
        "note at the top of the in-game mod settings window");

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    /// <summary>The live configs of the current session (each mod's own JsonConfig instance),
    /// non-empty ones only. Set per session from Initialize, cleared on Dispose.</summary>
    private static ImmutableArray<ModJsonConfig> s_configs = ImmutableArray<ModJsonConfig>.Empty;

    /// <summary>Resolves the session's live mod configs and (once) patches the escape menu.
    /// Called from the mod's Initialize for every game session.</summary>
    public static void Initialize(DependencyResolver resolver)
    {
        // ResolveAll needs the [MultiDependency] attribute, which only IConfig carries (not the
        // concrete ModJsonConfig), so resolve all configs and filter.
        var configs = new Lyst<ModJsonConfig>();
        foreach (IConfig config in resolver.ResolveAll<IConfig>().Implementations)
        {
            if (config is ModJsonConfig jsonConfig && !jsonConfig.IsEmpty)
            {
                configs.Add(jsonConfig);
            }
        }
        s_configs = configs.ToImmutableArray();
        tryApplyOnce();
        Log.Info($"ModConfig++: in-game mod settings serving {s_configs.Length} mod config(s).");
    }

    public static void Clear()
    {
        s_configs = ImmutableArray<ModJsonConfig>.Empty;
    }

    private static void tryApplyOnce()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        List<ConstructorInfo> ctors = AccessTools.GetDeclaredConstructors(typeof(GameEscapeMenu));
        if (ctors == null || ctors.Count != 1)
        {
            Log.Error("ModConfig++: GameEscapeMenu constructor not resolved; "
                + "in-game mod settings entry skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(ctors[0], postfix: new HarmonyMethod(typeof(InGameModSettings),
                nameof(EscapeMenuPostfix)));
            Log.Info("ModConfig++: in-game mod settings menu entry applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"ModConfig++: failed to patch the escape menu: {ex}");
        }
    }

    private static void EscapeMenuPostfix(GameEscapeMenu __instance)
    {
        try
        {
            if (s_configs.IsEmpty)
            {
                return;
            }
            ButtonText button = new ButtonText(BUTTON_TEXT.AsFormatted,
                () => openWindow(__instance)).Margin(1.pt());
            __instance.Body.Add(button);
            try
            {
                // Move the button from the end of the menu to just above the seed/version block.
                VisualElement root = button.RootElement;
                VisualElement parent = root.parent;
                if (parent != null && parent.childCount >= 2)
                {
                    root.RemoveFromHierarchy();
                    parent.Insert(parent.childCount - 1, root);
                }
            }
            catch (Exception)
            {
                // Leave the button at the bottom of the menu.
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void openWindow(GameEscapeMenu menu)
    {
        try
        {
            new ModSettingsWindow(s_configs).OpenIn(menu);
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    /// <summary>The dialog: the vanilla main-menu mod config window's look (scroll column on a
    /// dark panel, the vanilla parameter panel, a Restore defaults button), one section per mod.</summary>
    private sealed class ModSettingsWindow : Window
    {
        public ModSettingsWindow(ImmutableArray<ModJsonConfig> configs)
            : base(BUTTON_TEXT.AsFormatted)
        {
            DarkMask();
            CloseOnClickOutside();
            WindowWidth(500.px());
            WindowMaxHeight(70.Percent());
            ScrollColumn scroll = new ScrollColumn().Gap(2.pt()).Background(Theme.BackgroundDark)
                .Padding(4.pt());
            Body.Add(new Panel().Fill().BodyAdd(scroll));
            scroll.Add(new Label(NOTE_TEXT.AsFormatted).Color(Theme.InactiveColor));
            foreach (ModJsonConfig config in configs)
            {
                addModSection(scroll, config);
            }
        }

        private static void addModSection(ScrollColumn scroll, ModJsonConfig config)
        {
            scroll.Add(new Label(config.ModId.AsLoc()).FontBold().MarginTop(6.pt()));
            var panel = new ModJsonConfigPanel(config);
            scroll.Add(panel);
            scroll.Add(new ButtonText(Mafi.Unity.UiToolkit.Library.Button.General, Tr.RestoreDefaults)
                .AlignSelfEnd().MarginTop(2.pt()).OnClick((Action)delegate
            {
                config.ResetToDefaults();
                // ResetToDefaults does not fire OnValueChanged; re-set every parameter so
                // subscribed mods re-apply the defaults immediately.
                foreach (ModJsonConfigParam param in config.Parameters.Values)
                {
                    config.TrySetValue(param.Name, param.CurrentValue, out string _);
                }
                panel.RefreshValues();
            }, allowKeyPresses: false));
        }
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"ModConfig++: in-game mod settings failed (logged once): {ex}");
        }
    }
}
