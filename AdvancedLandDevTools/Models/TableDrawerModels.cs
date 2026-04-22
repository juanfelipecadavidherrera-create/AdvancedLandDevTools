// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AdvancedLandDevTools.Models
{
    public enum CellContentType { FreeText, LinkedProperty }

    public class LinkedPropertyRef
    {
        public string EntityHandle { get; set; } = "";
        public string EntityKind   { get; set; } = "";
        public string Property     { get; set; } = "";
        public string Format       { get; set; } = "";
        public string CachedValue  { get; set; } = "";
    }

    public class TableCell
    {
        public int             Row         { get; set; }
        public int             Col         { get; set; }
        public CellContentType ContentType { get; set; } = CellContentType.FreeText;
        public string          Text        { get; set; } = "";
        public LinkedPropertyRef? Link     { get; set; }
        public int  RowSpan       { get; set; } = 1;
        public int  ColSpan       { get; set; } = 1;
        public bool IsMergedSlave { get; set; } = false;

        [JsonIgnore]
        public string DisplayValue =>
            ContentType == CellContentType.LinkedProperty && Link != null
                ? (string.IsNullOrEmpty(Link.CachedValue) ? "(linked)" : Link.CachedValue)
                : Text;
    }

    public class TableDefinition
    {
        public string Id           { get; set; } = Guid.NewGuid().ToString("N");
        public string Name         { get; set; } = "Untitled Table";
        public int    Rows         { get; set; }
        public int    Cols         { get; set; }
        public List<double>    RowHeights { get; set; } = new();
        public List<double>    ColWidths  { get; set; } = new();
        public List<TableCell> Cells      { get; set; } = new();
        public DateTime CreatedUtc  { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>AutoCAD text style name to use for cell MText (e.g. "Standard", "Romans").</summary>
        public string TextStyleName { get; set; } = "Standard";
        /// <summary>Cell text height in drawing units (feet).</summary>
        public double TextHeight    { get; set; } = 2.5;

        /// <summary>Returns the master cell at (r, c), or null if that position is a slave or empty.</summary>
        public TableCell? this[int r, int c]
            => Cells.FirstOrDefault(x => x.Row == r && x.Col == c && !x.IsMergedSlave);

        /// <summary>Returns the cell at (r,c), creating it (as FreeText master) if absent.</summary>
        public TableCell GetOrCreate(int r, int c)
        {
            var cell = Cells.FirstOrDefault(x => x.Row == r && x.Col == c);
            if (cell == null)
            {
                cell = new TableCell { Row = r, Col = c };
                Cells.Add(cell);
            }
            return cell;
        }

        public static TableDefinition CreateDefault(int rows = 3, int cols = 3)
        {
            var td = new TableDefinition { Rows = rows, Cols = cols };
            for (int r = 0; r < rows; r++) td.RowHeights.Add(8.0);   // ft
            for (int c = 0; c < cols; c++) td.ColWidths.Add(20.0);   // ft
            return td;
        }

        public double TotalWidth  => ColWidths.Sum();
        public double TotalHeight => RowHeights.Sum();
    }

    /// <summary>
    /// Tracks the AutoCAD entity handles for a table that was drawn into model space.
    /// Used by <see cref="AdvancedLandDevTools.Engine.TableReactor"/> for auto-update.
    /// </summary>
    public class DrawnTable
    {
        public string TableId { get; set; } = "";
        /// <summary>Hex handles of the Line grid entities.</summary>
        public List<string> LineHandles { get; set; } = new();
        /// <summary>Maps "row,col" → hex handle of the MText entity for that cell.</summary>
        public Dictionary<string, string> CellMTextHandles { get; set; } = new();
    }

    public static class TableStorage
    {
        private static string Dir
        {
            get
            {
                var d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AdvancedLandDevTools", "Tables");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static void Save(TableDefinition td)
        {
            td.ModifiedUtc = DateTime.UtcNow;
            var path = Path.Combine(Dir, $"{td.Id}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(td, Formatting.Indented));
        }

        public static List<TableDefinition> LoadAll()
        {
            var list = new List<TableDefinition>();
            if (!Directory.Exists(Dir)) return list;

            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var td = JsonConvert.DeserializeObject<TableDefinition>(File.ReadAllText(f));
                    if (td != null)
                    {
                        td.Cells      ??= new();
                        td.RowHeights ??= new();
                        td.ColWidths  ??= new();
                        list.Add(td);
                    }
                }
                catch { }
            }
            return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void Delete(string id)
        {
            var path = Path.Combine(Dir, $"{id}.json");
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
