using System;
using System.Collections.Generic;
using System.IO;
using MacroKeyboard.Models;
using Newtonsoft.Json;

namespace MacroKeyboard.Services
{
    /// <summary>
    /// 宏持久化存储（JSON 文件）
    /// </summary>
    public class MacroStorage
    {
        private readonly string _storageDir;
        private readonly string _indexFile;

        public MacroStorage()
        {
            _storageDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MacroKeyboard");
            _indexFile = Path.Combine(_storageDir, "macros.json");

            if (!Directory.Exists(_storageDir))
                Directory.CreateDirectory(_storageDir);
        }

        public List<MacroDefinition> LoadAll()
        {
            if (!File.Exists(_indexFile))
                return new List<MacroDefinition>();

            try
            {
                var json = File.ReadAllText(_indexFile);
                var settings = new JsonSerializerSettings
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace
                };
                var macros = JsonConvert.DeserializeObject<List<MacroDefinition>>(json, settings) ?? new();

                // 清理可能因旧版 bug 积累的空白序列（保留至少一个非空序列）
                foreach (var macro in macros)
                {
                    if (macro.Sequences.Count > 1)
                    {
                        macro.Sequences.RemoveAll(s => s.Events.Count == 0);
                        if (macro.Sequences.Count == 0)
                            macro.Sequences.Add(new MacroSequence());
                    }
                }

                return macros;
            }
            catch
            {
                return new List<MacroDefinition>();
            }
        }

        public void SaveAll(List<MacroDefinition> macros)
        {
            var json = JsonConvert.SerializeObject(macros, Formatting.Indented);
            File.WriteAllText(_indexFile, json);
        }

        public void Save(MacroDefinition macro, List<MacroDefinition> allMacros)
        {
            var existing = allMacros.FindIndex(m => m.Id == macro.Id);
            if (existing >= 0)
                allMacros[existing] = macro;
            else
                allMacros.Add(macro);

            SaveAll(allMacros);
        }

        public void Delete(string macroId, List<MacroDefinition> allMacros)
        {
            allMacros.RemoveAll(m => m.Id == macroId);
            SaveAll(allMacros);
        }

        public string StorageDirectory => _storageDir;
    }
}
