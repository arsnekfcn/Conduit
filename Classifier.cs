using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Quartermaster
{
    // Config-driven classification Two user-editable subtype tables, both optional, both falling back to "unknown" with
    // the raw subtype preserved so the backend can classify later.
    //   classes.json : { "<coreBlockSubtypeId>": "<className>" }   -> ship class from a core block
    //   weapons.json : { "<weaponBlockSubtypeId>": "<category>" }  -> weapon category (vanilla + WeaponCore)
    // Vanilla weapons are also detected by interface in Scanner; this table is for WeaponCore/modded blocks.
    public class Classifier
    {
        private readonly Dictionary<string, string> _classBySubtype =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _weaponBySubtype =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Optional: pull a class out of the core block's CustomData, e.g. a line "Class=Cruiser".
        private readonly Regex _customDataClass = new Regex(@"(?im)^\s*class\s*[:=]\s*(.+?)\s*$");
        // Optional: pull a class out of the grid name if configured (set via a regex with a 'class' group).
        private Regex _gridNameClass;

        public static Classifier Load(QmConfig cfg)
        {
            var c = new Classifier();
            try
            {
                c.WriteExamplesOnce();
                c.LoadTable(ResolvePath(cfg.ClassTablePath, "classes.json"), c._classBySubtype);
                c.LoadTable(ResolvePath(cfg.WeaponTablePath, "weapons.json"), c._weaponBySubtype);
                if (!string.IsNullOrWhiteSpace(cfg.GridNameClassRegex))
                    c._gridNameClass = new Regex(cfg.GridNameClassRegex, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch (Exception ex) { Plugin.Log("Classifier load: " + ex.Message); }
            return c;
        }

        private static string ResolvePath(string configured, string defaultName) =>
            string.IsNullOrWhiteSpace(configured) ? Path.Combine(QmConfig.Dir, defaultName) : configured;

        private void LoadTable(string path, Dictionary<string, string> into)
        {
            if (!File.Exists(path)) return;
            var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
            if (map == null) return;
            foreach (var kv in map) into[kv.Key] = kv.Value;
        }

        // Class from a core block subtype (source = "subtype").
        public bool TryClassForSubtype(string subtype, out string className) =>
            _classBySubtype.TryGetValue(subtype ?? "", out className);

        // Category for a (likely modded/WeaponCore) weapon subtype. Null if not in the table.
        public string WeaponCategoryForSubtype(string subtype)
        {
            string cat;
            return _weaponBySubtype.TryGetValue(subtype ?? "", out cat) ? cat : null;
        }

        public string ClassFromCustomData(string customData)
        {
            if (string.IsNullOrEmpty(customData)) return null;
            var m = _customDataClass.Match(customData);
            return m.Success ? m.Groups[1].Value : null;
        }

        public string ClassFromGridName(string gridName)
        {
            if (_gridNameClass == null || string.IsNullOrEmpty(gridName)) return null;
            var m = _gridNameClass.Match(gridName);
            return m.Success ? (m.Groups["class"].Success ? m.Groups["class"].Value : m.Value) : null;
        }

        // Seed example tables next to config.json so the format is self-documenting (only written once).
        private void WriteExamplesOnce()
        {
            Directory.CreateDirectory(QmConfig.Dir);
            WriteExample(Path.Combine(QmConfig.Dir, "classes.example.json"),
                "{\n  \"_comment\": \"map a core/identity block SubtypeId to a ship class; copy to classes.json to use\",\n" +
                "  \"ShipCore_Cruiser\": \"Cruiser\",\n  \"ShipCore_Frigate\": \"Frigate\"\n}\n");
            WriteExample(Path.Combine(QmConfig.Dir, "weapons.example.json"),
                "{\n  \"_comment\": \"map a weapon block SubtypeId to a category; copy to weapons.json to use\",\n" +
                "  \"RailgunMk2\": \"WeaponCore\",\n  \"PDT\": \"Turret\"\n}\n");
        }

        private static void WriteExample(string path, string body)
        {
            try { if (!File.Exists(path)) File.WriteAllText(path, body); } catch { }
        }
    }
}
