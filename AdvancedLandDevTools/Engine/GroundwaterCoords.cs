using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>
    /// Shared coordinate conversion helpers used by both
    /// GroundwaterEngine (October) and GroundwaterMayEngine.
    /// </summary>
    internal static class GroundwaterCoords
    {
        internal static bool ConvertToLatLon(Document doc, Point3d drawingPoint,
                                             out double lat, out double lon)
        {
            lat = 0; lon = 0;

            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var civilDoc = Autodesk.Civil.ApplicationServices.CivilDocument
                        .GetCivilDocument(doc.Database);

                    string drawingCS = civilDoc.Settings.DrawingSettings
                        .UnitZoneSettings.CoordinateSystemCode;

                    if (string.IsNullOrEmpty(drawingCS))
                    {
                        doc.Editor.WriteMessage(
                            "\n  WARNING: No coordinate system assigned to drawing.");
                        doc.Editor.WriteMessage(
                            "\n  Falling back to approximate State Plane FL East conversion.");
                        tr.Commit();
                        return ConvertStatePlaneApprox(
                            drawingPoint.X, drawingPoint.Y, out lat, out lon);
                    }

                    doc.Editor.WriteMessage($"\n  Drawing coordinate system: {drawingCS}");

                    bool ok = TransformViaGeolocation(drawingCS, drawingPoint, out lat, out lon);
                    tr.Commit();

                    if (ok) return true;

                    doc.Editor.WriteMessage(
                        "\n  Geolocation API unavailable. Using approximate conversion.");
                    return ConvertStatePlaneApprox(
                        drawingPoint.X, drawingPoint.Y, out lat, out lon);
                }
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage($"\n  Transform exception: {ex.Message}");
                doc.Editor.WriteMessage("\n  Falling back to approximate conversion.");
                return ConvertStatePlaneApprox(
                    drawingPoint.X, drawingPoint.Y, out lat, out lon);
            }
        }

        private static bool TransformViaGeolocation(string drawingCS, Point3d pt,
                                                     out double lat, out double lon)
        {
            lat = 0; lon = 0;
            try
            {
                var asm = System.Reflection.Assembly.Load("Autodesk.Geolocation");
                if (asm == null) return false;

                var csType = asm.GetType("Autodesk.Geolocation.CoordinateSystem");
                var txType = asm.GetType("Autodesk.Geolocation.CoordinateSystemTransformer");
                if (csType == null || txType == null) return false;

                object sourceCRS = Activator.CreateInstance(csType, drawingCS)!;
                object targetCRS = Activator.CreateInstance(csType, "LL84")!;
                object transformer = Activator.CreateInstance(txType, sourceCRS, targetCRS)!;

                var transformMethod = txType.GetMethod("Transform",
                    new[] { typeof(Point3d) });
                if (transformMethod == null) return false;

                object result = transformMethod.Invoke(transformer, new object[] { pt })!;
                Point3d tgt = (Point3d)result;
                lon = tgt.X;
                lat = tgt.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ConvertStatePlaneApprox(
            double eastingFt, double northingFt, out double lat, out double lon)
        {
            double eastingM  = eastingFt  * 0.3048006096;
            double northingM = northingFt * 0.3048006096;

            double falseEastingM   = 200000.0;
            double centralMeridian = -81.0;
            double latOrigin       = 24.333333333;

            double dE = eastingM - falseEastingM;
            double dN = northingM;

            double metersPerDegreeLat = 110920.0;
            double metersPerDegreeLon = 99960.0;

            lat = latOrigin + (dN / metersPerDegreeLat);
            lon = centralMeridian + (dE / metersPerDegreeLon);

            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Reverse conversion — WGS84 → drawing coordinates
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets the drawing coordinate system code (opens its own transaction).
        /// Cache the result to avoid repeated transaction overhead.
        /// </summary>
        internal static string GetDrawingCoordinateSystem(Document doc)
        {
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var civilDoc = Autodesk.Civil.ApplicationServices.CivilDocument
                        .GetCivilDocument(doc.Database);
                    string cs = civilDoc.Settings.DrawingSettings
                        .UnitZoneSettings.CoordinateSystemCode;
                    tr.Commit();
                    return cs ?? "";
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// Converts WGS84 lat/lon to drawing coordinates using the specified CS code.
        /// </summary>
        internal static bool ConvertFromLatLon(string drawingCS, double lat, double lon,
                                               out double drawingX, out double drawingY)
        {
            drawingX = 0; drawingY = 0;

            if (!string.IsNullOrEmpty(drawingCS))
            {
                if (ReverseTransformViaGeolocation(drawingCS, lat, lon,
                        out drawingX, out drawingY))
                    return true;
            }

            return ConvertStatePlaneFromLatLon(lat, lon, out drawingX, out drawingY);
        }

        private static bool ReverseTransformViaGeolocation(
            string drawingCS, double lat, double lon,
            out double drawingX, out double drawingY)
        {
            drawingX = 0; drawingY = 0;
            try
            {
                var asm = System.Reflection.Assembly.Load("Autodesk.Geolocation");
                if (asm == null) return false;

                var csType = asm.GetType("Autodesk.Geolocation.CoordinateSystem");
                var txType = asm.GetType("Autodesk.Geolocation.CoordinateSystemTransformer");
                if (csType == null || txType == null) return false;

                // Reverse: WGS84 → drawing CS
                object sourceCRS = Activator.CreateInstance(csType, "LL84")!;
                object targetCRS = Activator.CreateInstance(csType, drawingCS)!;
                object transformer = Activator.CreateInstance(txType, sourceCRS, targetCRS)!;

                var transformMethod = txType.GetMethod("Transform",
                    new[] { typeof(Point3d) });
                if (transformMethod == null) return false;

                var wgsPoint = new Point3d(lon, lat, 0);
                object result = transformMethod.Invoke(transformer, new object[] { wgsPoint })!;
                Point3d tgt = (Point3d)result;
                drawingX = tgt.X;
                drawingY = tgt.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool ConvertStatePlaneFromLatLon(
            double lat, double lon, out double eastingFt, out double northingFt)
        {
            double metersPerDegreeLat = 110920.0;
            double metersPerDegreeLon = 99960.0;
            double falseEastingM   = 200000.0;
            double centralMeridian = -81.0;
            double latOrigin       = 24.333333333;

            double dN = (lat - latOrigin) * metersPerDegreeLat;
            double dE = (lon - centralMeridian) * metersPerDegreeLon;

            double eastingM  = dE + falseEastingM;
            double northingM = dN;

            eastingFt  = eastingM  / 0.3048006096;
            northingFt = northingM / 0.3048006096;

            return true;
        }
    }
}
