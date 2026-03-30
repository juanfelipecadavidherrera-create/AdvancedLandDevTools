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
    }
}
