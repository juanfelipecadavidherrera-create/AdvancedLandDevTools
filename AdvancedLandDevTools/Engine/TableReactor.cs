// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using System;
using System.Collections.Generic;
using System.Linq;
using AdvancedLandDevTools.Models;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>
    /// Session-scoped reactor that watches the entity handles linked in drawn tables.
    ///
    /// When a watched entity is modified, the reactor debounces the notification
    /// and defers all work to <c>Application.Idle</c> — never opening transactions,
    /// locking documents, or accessing DBObjects inside the <c>ObjectModified</c>
    /// event handler itself (which would crash Civil 3D).
    ///
    /// The reactor skips its own MText handles (stored in <c>_ownHandles</c>) so that
    /// updating cell text does not trigger an infinite notification loop.
    ///
    /// Lifecycle: created lazily via <see cref="Instance"/> on first table draw;
    /// shared for the duration of the Civil 3D session.
    /// </summary>
    public sealed class TableReactor
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static TableReactor? _instance;
        public  static TableReactor   Instance => _instance ??= new TableReactor();

        // ── State ─────────────────────────────────────────────────────────────

        // Entity handle (hex string) → list of watch entries
        private readonly Dictionary<string, List<WatchEntry>> _watches = new();

        // Table id → DrawnTable (entity handles for lines + MText)
        private readonly Dictionary<string, DrawnTable> _tables = new();

        // Table id → TableDefinition (in-memory model)
        private readonly Dictionary<string, TableDefinition> _defs = new();

        // Handles of MText we drew ourselves — skip these in OnObjectModified
        private readonly HashSet<string> _ownHandles = new();

        // ── Debounce state ────────────────────────────────────────────────────
        // Accumulate dirty entity handles during ObjectModified, then process
        // them all in a SINGLE Idle callback to avoid flooding.
        private readonly HashSet<string> _dirtyHandles = new();
        private bool _idlePending;

        // Guard flag — suppresses the reactor while we are drawing or refreshing
        // to prevent re-entrant modifications from causing crashes.
        private bool _suppressed;

        private bool _hooked;

        private TableReactor() { }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a newly drawn table for auto-update.
        /// Call this immediately after <see cref="TableDrawerEngine.DrawTable"/> returns.
        /// </summary>
        public void Register(Document doc, DrawnTable drawn, TableDefinition td)
        {
            _tables[td.Id] = drawn;
            _defs[td.Id]   = td;

            // Track our own MText handles so we can skip them in OnObjectModified.
            foreach (var h in drawn.CellMTextHandles.Values)
                _ownHandles.Add(h);

            // Build the entity-handle → watch-entry index.
            foreach (var cell in td.Cells.Where(c =>
                c.ContentType == CellContentType.LinkedProperty && c.Link != null))
            {
                string entityHandle = cell.Link!.EntityHandle;
                if (string.IsNullOrEmpty(entityHandle)) continue;

                if (!_watches.TryGetValue(entityHandle, out var list))
                {
                    list = new List<WatchEntry>();
                    _watches[entityHandle] = list;
                }
                list.Add(new WatchEntry(td.Id, cell.Row, cell.Col, cell.Link.Property));
            }

            HookDatabase(doc.Database);
        }

        /// <summary>Removes a table from the watch list (e.g. if the user erases it).</summary>
        public void Unregister(string tableId)
        {
            if (_tables.TryGetValue(tableId, out var drawn))
            {
                foreach (var h in drawn.CellMTextHandles.Values)
                    _ownHandles.Remove(h);
            }

            _tables.Remove(tableId);
            _defs.Remove(tableId);

            // Rebuild index without this table's entries.
            var toRemove = new List<string>();
            foreach (var kvp in _watches)
            {
                kvp.Value.RemoveAll(e => e.TableId == tableId);
                if (kvp.Value.Count == 0) toRemove.Add(kvp.Key);
            }
            foreach (var k in toRemove) _watches.Remove(k);
        }

        /// <summary>
        /// Suppresses the reactor temporarily. Use during drawing operations
        /// so that ObjectModified events from newly created entities don't
        /// trigger processing.
        /// </summary>
        public void Suppress()  => _suppressed = true;
        public void Resume()    => _suppressed = false;

        // ─────────────────────────────────────────────────────────────────────
        //  Private
        // ─────────────────────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────────────────────
        //  Private
        // ─────────────────────────────────────────────────────────────────────

        private void HookDatabase(Database db)
        {
            // Instead of Database.ObjectModified (which crashes Civil 3D pipe networks),
            // we hook the Document's CommandEnded event. This ensures we only read
            // entity data when the document is stable and no transactions are active.
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null && !_hooked)
            {
                doc.CommandEnded += OnCommandEnded;
                _hooked = true;
            }
        }

        /// <summary>
        /// Fires when any AutoCAD command finishes. We safely check all watched
        /// entities for changes. This avoids the severe instability of ObjectModified.
        /// </summary>
        private void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (_suppressed || _watches.Count == 0) return;

            // Skip transparent commands or our own refresh
            if (e.GlobalCommandName.StartsWith("'") || e.GlobalCommandName == "TABLEDRAW")
                return;

            _suppressed = true;
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                // We are outside any command, so we MUST lock the document
                // before opening a transaction.
                using var docLock = doc.LockDocument();
                using var tr = doc.Database.TransactionManager.StartTransaction();

                foreach (var kvp in _watches)
                {
                    string handleStr = kvp.Key;
                    var entries = kvp.Value;

                    // Resolve the entity
                    ObjectId entityId;
                    try
                    {
                        var h = new Handle(Convert.ToInt64(handleStr, 16));
                        if (!doc.Database.TryGetObjectId(h, out entityId) || entityId.IsErased)
                            continue;
                    }
                    catch { continue; }

                    // Extract all properties at once for this entity
                    var (_, opts) = TableDrawerEngine.ExtractProperties(entityId, doc.Database);

                    foreach (var entry in entries)
                    {
                        try
                        {
                            if (!_tables.TryGetValue(entry.TableId, out var drawn)) continue;
                            if (!_defs.TryGetValue(entry.TableId, out var td)) continue;

                            var cell = td.GetOrCreate(entry.Row, entry.Col);
                            if (cell.Link == null) continue;

                            string newVal = opts.Find(o => o.Name == entry.PropName)?.Value ?? "";

                            // If changed, update cache and model space
                            if (newVal != cell.Link.CachedValue)
                            {
                                cell.Link.CachedValue = newVal;
                                TableDrawerEngine.RefreshCellMText(
                                    doc, drawn, td, entry.Row, entry.Col, newVal);
                            }
                        }
                        catch { }
                    }
                }

                tr.Commit();
            }
            catch
            {
                // Non-fatal — swallow silently.
            }
            finally
            {
                _suppressed = false;
            }
        }

        private readonly record struct WatchEntry(string TableId, int Row, int Col, string PropName);
    }
}
