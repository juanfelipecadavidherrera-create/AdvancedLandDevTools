using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AdvancedLandDevTools.VehicleTracking.Commands
{
    /// <summary>
    /// Creates and manages dedicated layers for Vehicle Tracking output.
    /// </summary>
    public static class VtLayerManager
    {
        public const string OUTER_SWEEP  = "VT-SWEEP-OUTER";
        public const string INNER_SWEEP  = "VT-SWEEP-INNER";
        public const string PATH         = "VT-PATH";
        public const string VEHICLE      = "VT-VEHICLE";
        public const string WHEELS       = "VT-WHEELS";
        public const string COLLISION    = "VT-COLLISION";
        public const string PARKING      = "VT-PARKING";
        public const string ADA          = "VT-ADA";
        public const string LABELS       = "VT-LABELS";

        private static readonly (string Name, short ColorIndex)[] _layers = new[]
        {
            (OUTER_SWEEP, (short)1),   // Red
            (INNER_SWEEP, (short)3),   // Green
            (PATH,        (short)4),   // Cyan
            (VEHICLE,     (short)5),   // Blue
            (WHEELS,      (short)40),  // Orange-brown (wheel rectangles)
            (COLLISION,   (short)6),   // Magenta
            (PARKING,     (short)7),   // White
            (ADA,         (short)30),  // Orange
            (LABELS,      (short)2),   // Yellow
        };

        /// <summary>
        /// Ensure all VT layers exist in the database. Call inside an open transaction.
        /// </summary>
        public static void EnsureLayers(Database db, Transaction tx)
        {
            var lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);

            foreach (var (name, colorIdx) in _layers)
            {
                if (lt.Has(name)) continue;

                lt.UpgradeOpen();
                var lr = new LayerTableRecord
                {
                    Name = name,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, colorIdx)
                };
                lt.Add(lr);
                tx.AddNewlyCreatedDBObject(lr, true);
            }
        }

        /// <summary>Get the ObjectId of a VT layer (must already exist).</summary>
        public static ObjectId GetLayerId(Database db, Transaction tx, string layerName)
        {
            var lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
            return lt.Has(layerName) ? lt[layerName] : db.Clayer;
        }
    }
}
