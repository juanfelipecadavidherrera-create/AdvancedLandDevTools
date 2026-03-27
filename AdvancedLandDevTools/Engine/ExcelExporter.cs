using System;
using System.IO;
using System.Linq;
using System.Text;
using AdvancedLandDevTools.Models;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>
    /// Exports an AreaManagerProject to a formatted .xlsx file.
    /// Uses raw Open XML (zip + XML) to avoid OpenXml SDK version issues.
    /// </summary>
    public static class ExcelExporter
    {
        public static void Export(string filePath, AreaManagerProject project)
        {
            // Group areas by category
            var grouped = project.Areas
                .GroupBy(a => string.IsNullOrEmpty(a.Category) ? "(Uncategorized)" : a.Category)
                .OrderBy(g => g.Key == "(Uncategorized)" ? "zzz" : g.Key)
                .ToList();

            double grandTotal = project.Areas.Sum(a => a.AreaSqFt);
            int totalCount = project.Areas.Count;

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

            // Column widths
            sb.AppendLine("<cols>");
            sb.AppendLine("  <col min=\"1\" max=\"1\" width=\"8\" customWidth=\"1\"/>");
            sb.AppendLine("  <col min=\"2\" max=\"2\" width=\"35\" customWidth=\"1\"/>");
            sb.AppendLine("  <col min=\"3\" max=\"3\" width=\"20\" customWidth=\"1\"/>");
            sb.AppendLine("  <col min=\"4\" max=\"4\" width=\"22\" customWidth=\"1\"/>");
            sb.AppendLine("  <col min=\"5\" max=\"5\" width=\"22\" customWidth=\"1\"/>");
            sb.AppendLine("  <col min=\"6\" max=\"6\" width=\"14\" customWidth=\"1\"/>");
            sb.AppendLine("</cols>");

            sb.AppendLine("<sheetData>");

            uint row = 1;

            // ── Title ──
            AppendRow(sb, row++, new[] {
                TCell("A", $"AREA REPORT — {Esc(project.ProjectName.ToUpper())}", 1),
                TCell("B", "", 0), TCell("C", "", 0),
                TCell("D", "", 0), TCell("E", "", 0)
            });
            AppendRow(sb, row++, new[] {
                TCell("A", $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}", 2),
                TCell("B", "", 0)
            });

            // Blank
            AppendRow(sb, row++, System.Array.Empty<CellDef>());

            // ── DETAIL SECTION ──
            int itemNum = 1;
            foreach (var group in grouped)
            {
                double catTotal = group.Sum(a => a.AreaSqFt);

                // Category header
                AppendRow(sb, row++, new[] {
                    TCell("A", $"▌ {Esc(group.Key)}", 4),
                    TCell("B", "", 4),
                    TCell("C", "", 4),
                    NCell("D", catTotal, 5),
                    TCell("E", $"{group.Count()} area(s)", 4),
                    TCell("F", "", 4)
                });

                // Column headers
                AppendRow(sb, row++, new[] {
                    TCell("A", "#", 3),
                    TCell("B", "Name", 3),
                    TCell("C", "Layer", 3),
                    TCell("D", "Area (sq ft)", 3),
                    TCell("E", "Category", 3),
                    TCell("F", "% of Cat.", 3)
                });

                // Data rows
                bool alt = false;
                foreach (var area in group)
                {
                    uint si = alt ? 7u : 6u;
                    uint ni = alt ? 9u : 8u;
                    double pctCat = catTotal > 0 ? (area.AreaSqFt / catTotal) * 100 : 0;

                    AppendRow(sb, row++, new[] {
                        NCell("A", itemNum++, si),
                        TCell("B", Esc(area.Name), si),
                        TCell("C", Esc(area.Layer), si),
                        NCell("D", area.AreaSqFt, ni),
                        TCell("E", Esc(area.Category), si),
                        NCell("F", pctCat, ni)
                    });
                    alt = !alt;
                }

                // Blank separator
                AppendRow(sb, row++, System.Array.Empty<CellDef>());
            }

            // ── SUMMARY SECTION ──
            AppendRow(sb, row++, new[] {
                TCell("A", "SUMMARY", 1),
                TCell("B", "", 0), TCell("C", "", 0),
                TCell("D", "", 0), TCell("E", "", 0)
            });

            AppendRow(sb, row++, new[] {
                TCell("A", "Category", 3),
                TCell("B", "", 3),
                TCell("C", "Count", 3),
                TCell("D", "Total Area (sq ft)", 3),
                TCell("E", "% of Total", 3)
            });

            bool sumAlt = false;
            foreach (var g in grouped)
            {
                double catTotal = g.Sum(a => a.AreaSqFt);
                double pct = grandTotal > 0 ? (catTotal / grandTotal) * 100 : 0;
                uint si = sumAlt ? 7u : 6u;
                uint ni = sumAlt ? 9u : 8u;

                AppendRow(sb, row++, new[] {
                    TCell("A", Esc(g.Key), si),
                    TCell("B", "", si),
                    NCell("C", g.Count(), si),
                    NCell("D", catTotal, ni),
                    NCell("E", pct, ni)
                });
                sumAlt = !sumAlt;
            }

            // Grand total
            AppendRow(sb, row++, new[] {
                TCell("A", "GRAND TOTAL", 10),
                TCell("B", "", 10),
                NCell("C", totalCount, 10),
                NCell("D", grandTotal, 11),
                NCell("E", 100.0, 11)
            });

            sb.AppendLine("</sheetData>");

            // Merge cells
            sb.AppendLine("<mergeCells>");
            sb.AppendLine("  <mergeCell ref=\"A1:E1\"/>");
            sb.AppendLine("  <mergeCell ref=\"A2:E2\"/>");
            sb.AppendLine("</mergeCells>");

            sb.AppendLine("</worksheet>");

            // Build the xlsx (it's a zip file)
            WriteXlsx(filePath, sb.ToString(), project.ProjectName);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Build xlsx as a zip with proper Open XML structure
        // ═════════════════════════════════════════════════════════════════════

        private static void WriteXlsx(string filePath, string sheetXml, string projectName)
        {
            if (File.Exists(filePath)) File.Delete(filePath);

            using var zip = System.IO.Compression.ZipFile.Open(filePath,
                System.IO.Compression.ZipArchiveMode.Create, Encoding.UTF8);

            // [Content_Types].xml
            AddEntry(zip, "[Content_Types].xml",
@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>");

            // _rels/.rels
            AddEntry(zip, "_rels/.rels",
@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");

            // xl/_rels/workbook.xml.rels
            AddEntry(zip, "xl/_rels/workbook.xml.rels",
@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>");

            // xl/workbook.xml
            AddEntry(zip, "xl/workbook.xml",
@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets>
    <sheet name=""Area Report"" sheetId=""1"" r:id=""rId1""/>
  </sheets>
</workbook>");

            // xl/styles.xml — themed styles
            AddEntry(zip, "xl/styles.xml", BuildStylesXml());

            // xl/worksheets/sheet1.xml
            AddEntry(zip, "xl/worksheets/sheet1.xml", sheetXml);
        }

        private static void AddEntry(System.IO.Compression.ZipArchive zip, string path, string content)
        {
            var entry = zip.CreateEntry(path, System.IO.Compression.CompressionLevel.Fastest);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(content);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Styles XML — dark themed with proper formatting
        // ═════════════════════════════════════════════════════════════════════

        private static string BuildStylesXml()
        {
            return
@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <numFmts count=""2"">
    <numFmt numFmtId=""164"" formatCode=""#,##0.00""/>
    <numFmt numFmtId=""165"" formatCode=""0.0""/>
  </numFmts>
  <fonts count=""6"">
    <font><sz val=""11""/><color rgb=""FF333333""/><name val=""Segoe UI""/></font>
    <font><b/><sz val=""14""/><color rgb=""FF0078D4""/><name val=""Segoe UI""/></font>
    <font><sz val=""10""/><color rgb=""FF757575""/><name val=""Segoe UI""/></font>
    <font><b/><sz val=""11""/><color rgb=""FFFFFFFF""/><name val=""Segoe UI""/></font>
    <font><b/><sz val=""12""/><color rgb=""FF0078D4""/><name val=""Segoe UI""/></font>
    <font><b/><sz val=""11""/><color rgb=""FF0078D4""/><name val=""Segoe UI""/></font>
  </fonts>
  <fills count=""8"">
    <fill><patternFill patternType=""none""/></fill>
    <fill><patternFill patternType=""gray125""/></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFFFFFF""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF0078D4""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFF0F6FF""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFF5F5F5""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFE8F4FD""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFFFFFF""/></patternFill></fill>
  </fills>
  <borders count=""2"">
    <border/>
    <border>
      <left/><right/><top/>
      <bottom style=""thin""><color rgb=""FFD0D0D0""/></bottom>
      <diagonal/>
    </border>
  </borders>
  <cellXfs count=""12"">
    <cellXf fontId=""0"" fillId=""2"" borderId=""0"" xfId=""0"" applyFill=""1""/>
    <cellXf fontId=""1"" fillId=""2"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyAlignment=""1""><alignment vertical=""center""/></cellXf>
    <cellXf fontId=""2"" fillId=""2"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1""/>
    <cellXf fontId=""3"" fillId=""3"" borderId=""1"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyBorder=""1""/>
    <cellXf fontId=""4"" fillId=""4"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1""/>
    <cellXf fontId=""5"" fillId=""4"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyNumberFormat=""1"" numFmtId=""164""/>
    <cellXf fontId=""0"" fillId=""7"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1""/>
    <cellXf fontId=""0"" fillId=""5"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1""/>
    <cellXf fontId=""0"" fillId=""7"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyNumberFormat=""1"" numFmtId=""164""/>
    <cellXf fontId=""0"" fillId=""5"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyNumberFormat=""1"" numFmtId=""164""/>
    <cellXf fontId=""5"" fillId=""6"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1""/>
    <cellXf fontId=""5"" fillId=""6"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyNumberFormat=""1"" numFmtId=""164""/>
  </cellXfs>
</styleSheet>";
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Cell XML helpers
        // ═════════════════════════════════════════════════════════════════════

        private static void AppendRow(StringBuilder sb, uint rowIdx, CellDef[] cells)
        {
            sb.Append($"<row r=\"{rowIdx}\">");
            foreach (var c in cells)
            {
                if (c.IsNumber)
                    sb.Append($"<c r=\"{c.Col}{rowIdx}\" s=\"{c.Style}\"><v>{c.NumVal}</v></c>");
                else
                    sb.Append($"<c r=\"{c.Col}{rowIdx}\" s=\"{c.Style}\" t=\"inlineStr\"><is><t>{c.Text}</t></is></c>");
            }
            sb.AppendLine("</row>");
        }

        private struct CellDef
        {
            public string Col;
            public string Text;
            public double NumVal;
            public uint Style;
            public bool IsNumber;
        }

        private static CellDef TCell(string col, string text, uint style)
            => new CellDef { Col = col, Text = text, Style = style, IsNumber = false };

        private static CellDef NCell(string col, double val, uint style)
            => new CellDef { Col = col, NumVal = val, Style = style, IsNumber = true };

        private static string Esc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
