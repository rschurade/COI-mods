# Translating Shipping++

Captain of Industry only loads its own translation files, so Shipping++ ships its own: every
string of the mod is looked up here first and falls back to English.

## Adding a language

1. Copy `en.json` to the file name the game uses for your language — the same names as in
   `<game>/Translations/`: `de.json`, `fr.json`, `pl.json`, `zh_Hans.json`, …
2. Translate the **second** string of each pair. Leave the first one (the id) untouched — it is
   what links the text to the mod.
3. Keep the placeholders (`{0}`, `{1}`) intact, including their count. A line whose placeholders
   are broken falls back to the untranslated pattern (and logs a warning).
4. Drop the file into the mod folder's `Translations` directory
   (`<user data>/Captain of Industry/Mods/ShippingPP/Translations/` for a local install) and
   restart the game with your language selected. The log line
   `Shipping++: loaded N translated strings from '<file>'` confirms it was picked up.

Strings not listed in the file simply stay English, so a partial translation is fine.

Translations are welcome as pull requests at https://github.com/rschurade/COI-mods.

## Regenerating `en.json`

`en.json` is generated from the mod itself, so it always matches the installed version:

1. Create an empty file named `EXPORT_TEMPLATE` (no extension) in the installed mod's
   `Translations` directory.
2. Start the game and load any save. `en.json` is rewritten with every string of the mod, and
   the flag file is deleted again so no further start overwrites it.

Note that strings the vanilla game already provides (Assign, Unassign, Add stop, Show on the map,
Cargo ships, …) are not part of this file: those come from the game's own translations.
