#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using IamZhuli.Presenter;
using UnityEngine;

namespace IamZhuli.Unity
{
    /// <summary>
    /// 本地存档服务:PersistentData/saves/{slot}.json,序列化走 Presenter.SaveJson
    /// (零依赖/decimal 精确,与 Web 壳 STJ 存档文件互通)。
    /// 目录可注入(EditMode 测试用临时目录,不污染编辑器 persistentDataPath)。
    /// </summary>
    public sealed class SaveService
    {
        private readonly string _dir;

        public SaveService() : this(Path.Combine(Application.persistentDataPath, "saves")) { }
        public SaveService(string dir) => _dir = dir;

        /// <summary>存档(调用方须先过 GameCore.CanSave 门禁)。返回错误信息,null=成功。</summary>
        public string? Save(string slot, GameCore core)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, SlotFile(slot)), SaveJson.Serialize(core.ExportSave()));
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>读档。无存档/坏档返回 null(坏档不抛,与列表口径一致:跳过)。</summary>
        public SaveData? Load(string slot)
        {
            try
            {
                var path = Path.Combine(_dir, SlotFile(slot));
                return File.Exists(path) ? SaveJson.Parse(File.ReadAllText(path)) : null;
            }
            catch { return null; }
        }

        public readonly struct SaveMeta
        {
            public readonly string Slot, LevelId;
            public readonly int Day;
            public readonly DateTime Mtime;
            public SaveMeta(string slot, string levelId, int day, DateTime mtime)
            { Slot = slot; LevelId = levelId; Day = day; Mtime = mtime; }
        }

        /// <summary>存档列表(新→旧);坏档跳过。</summary>
        public List<SaveMeta> ListSaves()
        {
            var list = new List<SaveMeta>();
            if (!Directory.Exists(_dir)) return list;
            foreach (var f in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    var sd = SaveJson.Parse(File.ReadAllText(f));
                    list.Add(new SaveMeta(Path.GetFileNameWithoutExtension(f), sd.LevelId, sd.Day,
                        File.GetLastWriteTime(f)));
                }
                catch { /* 坏档跳过 */ }
            }
            list.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));
            return list;
        }

        /// <summary>槽位名清洗(防路径穿越:只留字母数字-_ )。</summary>
        private static string SlotFile(string slot)
        {
            var clean = new string(Array.FindAll(slot.ToCharArray(), c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
            if (clean.Length == 0) throw new ArgumentException("槽位名非法: " + slot);
            return clean + ".json";
        }
    }
}
