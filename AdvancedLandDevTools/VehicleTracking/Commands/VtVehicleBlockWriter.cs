using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AdvancedLandDevTools.VehicleTracking.Core;
using AdvancedLandDevTools.VehicleTracking.Data;

namespace AdvancedLandDevTools.VehicleTracking.Commands
{
    /// <summary>
    /// Creates and inserts a detailed vehicle plan-view block showing
    /// the vehicle body, wheels, mirrors, cab detail, and annotated
    /// dimensions (length, width, wheelbase, overhangs, turning radius).
    /// </summary>
    public static class VtVehicleBlockWriter
    {
        // ── Layout constants (feet) ─────────────────────────────────
        private const double TEXT_H     = 0.8;
        private const double DIM_OFF    = 4.0;   // first dim line offset from body
        private const double DIM_SPC    = 2.8;   // spacing between dim rows
        private const double TICK       = 0.45;  // tick mark half-size
        private const double EXT_GAP    = 0.35;  // gap before extension line starts
        private const double WHEEL_L    = 2.2;   // wheel rectangle length
        private const double WHEEL_W    = 0.75;  // wheel rectangle width
        private const double MIRROR_L   = 1.2;   // side mirror length
        private const double MIRROR_W   = 0.18;  // side mirror width
        private const double BUMPER_D   = 0.35;  // bumper depth (protrusion)

        // ── Color indices ───────────────────────────────────────────
        private const short C_BODY      = 7;     // White — body outline
        private const short C_CAB       = 4;     // Cyan  — cab / windshield
        private const short C_WHEEL     = 8;     // Dark gray — wheels
        private const short C_DIM       = 2;     // Yellow — dimension lines + text
        private const short C_TITLE     = 5;     // Blue   — title text
        private const short C_ARROW     = 1;     // Red    — front direction arrow
        private const short C_AXLE      = 251;   // Light gray — axle centerlines
        private const short C_DETAIL    = 253;   // Pale gray  — bumpers, grille
        private const short C_TRAILER   = 150;   // Dark blue  — trailer body
        private const short C_COUPLING  = 30;    // Orange     — coupling point

        // ═══════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Ensure a block definition exists for the given vehicle.
        /// Returns the BlockTableRecord ObjectId.
        /// </summary>
        public static ObjectId EnsureBlockDef(
            Database db, Transaction tx,
            VehicleUnit vehicle, string symbol, string category,
            ArticulatedVehicle? artic = null)
        {
            var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            string name = $"VT_DETAIL_{symbol}";
            if (bt.Has(name)) return bt[name];

            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name };
            var id = bt.Add(btr);
            tx.AddNewlyCreatedDBObject(btr, true);

            if (artic != null && artic.Trailers.Length > 0)
                BuildArticulated(btr, tx, artic);
            else
                BuildSingle(btr, tx, vehicle, symbol, category);

            return id;
        }

        /// <summary>
        /// Insert the vehicle detail block at the user-specified point.
        /// </summary>
        public static ObjectId InsertBlock(
            Database db, Transaction tx, BlockTableRecord modelSpace,
            ObjectId blockDefId, Point3d insertPt, double rotation = 0.0)
        {
            VtLayerManager.EnsureLayers(db, tx);
            var br = new BlockReference(insertPt, blockDefId)
            {
                Rotation = rotation,
                LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.VEHICLE)
            };
            modelSpace.AppendEntity(br);
            tx.AddNewlyCreatedDBObject(br, true);
            return br.ObjectId;
        }

        // ═══════════════════════════════════════════════════════════
        //  Single-unit vehicle
        // ═══════════════════════════════════════════════════════════

        private static void BuildSingle(
            BlockTableRecord btr, Transaction tx,
            VehicleUnit v, string symbol, string category)
        {
            // Key geometry (origin = geometric center of body)
            double halfL = v.Length / 2.0;
            double halfW = v.Width / 2.0;
            double fX = halfL;                       // front face
            double rX = -halfL;                      // rear face
            double faX = fX - v.FrontOverhang;       // front axle
            double raX = faX - v.Wheelbase;           // rear axle
            double ht = v.TrackWidth / 2.0;           // half track width
            double ch = Math.Min(1.2, v.Width * 0.12); // front corner chamfer

            // 1 ── Body outline (chamfered front) ────────────────────
            DrawBodyOutline(btr, tx, fX, rX, halfW, ch, C_BODY);

            // 2 ── Cab detail ────────────────────────────────────────
            DrawCab(btr, tx, v, fX, halfW, faX, category);

            // 3 ── Bumpers ───────────────────────────────────────────
            DrawBumpers(btr, tx, fX, rX, halfW);

            // 4 ── Wheels (4 corners) ────────────────────────────────
            DrawWheel(btr, tx, faX, ht);
            DrawWheel(btr, tx, faX, -ht);
            DrawWheel(btr, tx, raX, ht);
            DrawWheel(btr, tx, raX, -ht);

            // 5 ── Axle centerlines ──────────────────────────────────
            DrawAxleCenterline(btr, tx, faX, halfW + 2.0);
            DrawAxleCenterline(btr, tx, raX, halfW + 2.0);

            // 6 ── Side mirrors ──────────────────────────────────────
            double mirX = faX + 1.5;
            DrawMirror(btr, tx, mirX, halfW);
            DrawMirror(btr, tx, mirX, -halfW);

            // 7 ── Front direction arrow ─────────────────────────────
            DrawFrontArrow(btr, tx, fX + 1.2, 0);

            // 8 ── Dimensions ────────────────────────────────────────
            DrawSingleDimensions(btr, tx, v, fX, rX, halfW, faX, raX);

            // 9 ── Title block ───────────────────────────────────────
            DrawTitleBlock(btr, tx, v.Name, symbol,
                v.Length, v.Width, v.Wheelbase, v.EffectiveMinRadius,
                rX, halfW);
        }

        // ═══════════════════════════════════════════════════════════
        //  Articulated vehicle (tractor + trailers)
        // ═══════════════════════════════════════════════════════════

        private static void BuildArticulated(
            BlockTableRecord btr, Transaction tx,
            ArticulatedVehicle av)
        {
            var lead = av.LeadUnit;
            // Place the tractor at the front of the combination
            // Origin = center of total length
            double totalHalfL = av.TotalLength / 2.0;

            // Tractor front X
            double tFrontX = totalHalfL;
            double tRearX = tFrontX - lead.Length;
            double tHalfW = lead.Width / 2.0;
            double tFaX = tFrontX - lead.FrontOverhang;
            double tRaX = tFaX - lead.Wheelbase;
            double tHt = lead.TrackWidth / 2.0;
            double tCh = Math.Min(1.2, lead.Width * 0.12);

            // ── Draw tractor ────────────────────────────────────────
            DrawBodyOutline(btr, tx, tFrontX, tRearX, tHalfW, tCh, C_BODY);
            DrawCab(btr, tx, lead, tFrontX, tHalfW, tFaX, "Semi");
            DrawBumpers(btr, tx, tFrontX, tRearX, tHalfW);
            DrawWheel(btr, tx, tFaX, tHt);
            DrawWheel(btr, tx, tFaX, -tHt);
            DrawWheel(btr, tx, tRaX, tHt);
            DrawWheel(btr, tx, tRaX, -tHt);
            DrawAxleCenterline(btr, tx, tFaX, tHalfW + 2.0);
            DrawAxleCenterline(btr, tx, tRaX, tHalfW + 2.0);
            double mirX = tFaX + 1.5;
            DrawMirror(btr, tx, mirX, tHalfW);
            DrawMirror(btr, tx, mirX, -tHalfW);
            DrawFrontArrow(btr, tx, tFrontX + 1.2, 0);

            // ── Draw each trailer ───────────────────────────────────
            double couplingX = tRaX; // start coupling from tractor rear axle
            foreach (var trailer in av.Trailers)
            {
                var tu = trailer.Unit;
                var cp = trailer.Coupling;
                double halfTrW = tu.Width / 2.0;
                double trHt = tu.TrackWidth / 2.0;

                // Coupling point X
                double hitchX = couplingX - cp.HitchOffset;
                // Trailer front is at kingpin
                double trFrontX = hitchX + cp.KingpinOffset;
                double trRearX = trFrontX - tu.Length;
                double trFaX = trFrontX - tu.FrontOverhang;
                double trRaX = trFaX - tu.Wheelbase;

                // Coupling circle
                AddCircle(btr, tx, hitchX, 0, 0.6, C_COUPLING);
                // Coupling connector line
                AddLine(btr, tx, couplingX, 0, hitchX, 0, C_COUPLING);

                // Trailer body (no chamfer — flat face)
                DrawBodyOutline(btr, tx, trFrontX, trRearX, halfTrW, 0.0, C_TRAILER);

                // Trailer wheels (rear axle — typically tandem shown as single pair)
                DrawWheel(btr, tx, trRaX, trHt);
                DrawWheel(btr, tx, trRaX, -trHt);
                DrawAxleCenterline(btr, tx, trRaX, halfTrW + 2.0);

                // Trailer front axle (if wheelbase < length significantly)
                if (tu.FrontOverhang > 2.0)
                {
                    DrawWheel(btr, tx, trFaX, trHt);
                    DrawWheel(btr, tx, trFaX, -trHt);
                    DrawAxleCenterline(btr, tx, trFaX, halfTrW + 2.0);
                }

                // Rear bumper for trailer
                AddLine(btr, tx, trRearX - BUMPER_D, halfTrW * 0.9,
                                 trRearX - BUMPER_D, -halfTrW * 0.9, C_DETAIL);

                couplingX = trRaX; // next trailer couples from this rear axle
            }

            // ── Overall dimensions ──────────────────────────────────
            double maxHalfW = tHalfW;
            foreach (var t in av.Trailers)
                maxHalfW = Math.Max(maxHalfW, t.Unit.Width / 2.0);

            // Total length (top)
            double dimY = maxHalfW + DIM_OFF;
            DrawHDim(btr, tx, totalHalfL, -totalHalfL, dimY, $"L = {av.TotalLength:F1}'");

            // Tractor length (second row top)
            DrawHDim(btr, tx, tFrontX, tRearX, dimY + DIM_SPC,
                $"Tractor = {lead.Length:F1}'");

            // Tractor wheelbase (bottom row 1)
            double dimYBot = -maxHalfW - DIM_OFF;
            DrawHDim(btr, tx, tFaX, tRaX, dimYBot, $"WB = {lead.Wheelbase:F1}'");

            // Overall width (right)
            double dimXR = totalHalfL + DIM_OFF;
            DrawVDim(btr, tx, tHalfW, -tHalfW, dimXR, $"W = {lead.Width:F1}'");

            // Title
            DrawTitleBlock(btr, tx, av.Name, av.Symbol,
                av.TotalLength, lead.Width, lead.Wheelbase, lead.EffectiveMinRadius,
                -totalHalfL, maxHalfW);
        }

        // ═══════════════════════════════════════════════════════════
        //  Body outline — rectangle with optional chamfered front
        // ═══════════════════════════════════════════════════════════

        private static void DrawBodyOutline(
            BlockTableRecord btr, Transaction tx,
            double fX, double rX, double halfW, double chamfer, short color)
        {
            var body = new Polyline();
            int v = 0;

            if (chamfer > 0.1)
            {
                // CW with chamfered front corners (arc bulge for smooth rounding)
                double b = -Math.Tan(Math.PI / 8.0); // CW 90° arc = convex outside corner
                body.AddVertexAt(v++, new Point2d(rX, halfW), 0, 0, 0);
                body.AddVertexAt(v++, new Point2d(fX - chamfer, halfW), b, 0, 0);
                body.AddVertexAt(v++, new Point2d(fX, halfW - chamfer), 0, 0, 0);
                body.AddVertexAt(v++, new Point2d(fX, -(halfW - chamfer)), b, 0, 0);
                body.AddVertexAt(v++, new Point2d(fX - chamfer, -halfW), 0, 0, 0);
                body.AddVertexAt(v++, new Point2d(rX, -halfW), 0, 0, 0);
            }
            else
            {
                // Plain rectangle (trailers, etc.)
                body.AddVertexAt(v++, new Point2d(rX, halfW), 0, 0, 0);
                body.AddVertexAt(v++, new Point2d(fX, halfW), 0, 0, 0);
                body.AddVertexAt(v++, new Point2d(fX, -halfW), 0, 0, 0);
                body.AddVertexAt(v++, new Point2d(rX, -halfW), 0, 0, 0);
            }

            body.Closed = true;
            body.ColorIndex = color;
            body.LineWeight = LineWeight.LineWeight035;
            btr.AppendEntity(body);
            tx.AddNewlyCreatedDBObject(body, true);
        }

        // ═══════════════════════════════════════════════════════════
        //  Cab detail — windshield, hood lines, headlights
        // ═══════════════════════════════════════════════════════════

        private static void DrawCab(
            BlockTableRecord btr, Transaction tx,
            VehicleUnit v, double fX, double halfW, double faX, string category)
        {
            bool isTruck = category != "Passenger" && category != "Recreational";

            // Windshield line — across the cab
            double wsX = faX + (isTruck ? 1.5 : 2.5);
            if (wsX > fX - 0.5) wsX = fX - 0.5;
            double wsInset = 0.4; // windshield inset from body edge
            AddLine(btr, tx, wsX, halfW - wsInset, wsX, -(halfW - wsInset), C_CAB);

            // A-pillar lines — from windshield corners to front body corners
            double pillarFrontX = fX - 0.3;
            AddLine(btr, tx, wsX, halfW - wsInset, pillarFrontX, halfW - 0.15, C_CAB);
            AddLine(btr, tx, wsX, -(halfW - wsInset), pillarFrontX, -(halfW - 0.15), C_CAB);

            // Hood panel line — center line on hood
            double hoodMidX = (wsX + fX) / 2.0;
            AddLine(btr, tx, hoodMidX, halfW * 0.3, hoodMidX, -halfW * 0.3, C_DETAIL);

            // Grille lines at front face
            double grilleInset = 0.3;
            for (int i = 0; i < 3; i++)
            {
                double gx = fX - 0.15;
                double gy = halfW * 0.5 - i * (halfW * 0.35);
                // Short horizontal grille bar
                AddLine(btr, tx, gx, gy - 0.15, gx, gy + 0.15, C_DETAIL);
            }

            // Headlights — small rectangles at front corners
            double hlX = fX - 0.25;
            DrawSmallRect(btr, tx, hlX, halfW - grilleInset, 0.5, 0.4, C_CAB);
            DrawSmallRect(btr, tx, hlX, -(halfW - grilleInset), 0.5, 0.4, C_CAB);

            // Taillights at rear
            double rX = fX - v.Length;
            double tlX = rX + 0.2;
            DrawSmallRect(btr, tx, tlX, halfW - 0.3, 0.35, 0.5, C_ARROW);
            DrawSmallRect(btr, tx, tlX, -(halfW - 0.3), 0.35, 0.5, C_ARROW);
        }

        // ═══════════════════════════════════════════════════════════
        //  Bumpers
        // ═══════════════════════════════════════════════════════════

        private static void DrawBumpers(
            BlockTableRecord btr, Transaction tx,
            double fX, double rX, double halfW)
        {
            // Front bumper — slightly protruding
            double fw = halfW * 0.85;
            AddLine(btr, tx, fX + BUMPER_D, fw, fX + BUMPER_D, -fw, C_DETAIL);
            AddLine(btr, tx, fX, fw, fX + BUMPER_D, fw, C_DETAIL);
            AddLine(btr, tx, fX, -fw, fX + BUMPER_D, -fw, C_DETAIL);

            // Rear bumper
            double rw = halfW * 0.9;
            AddLine(btr, tx, rX - BUMPER_D, rw, rX - BUMPER_D, -rw, C_DETAIL);
            AddLine(btr, tx, rX, rw, rX - BUMPER_D, rw, C_DETAIL);
            AddLine(btr, tx, rX, -rw, rX - BUMPER_D, -rw, C_DETAIL);
        }

        // ═══════════════════════════════════════════════════════════
        //  Wheel — rectangle at axle/track position
        // ═══════════════════════════════════════════════════════════

        private static void DrawWheel(
            BlockTableRecord btr, Transaction tx,
            double axleX, double trackY)
        {
            // Wheel rectangle centered on (axleX, trackY)
            double x1 = axleX - WHEEL_L / 2;
            double x2 = axleX + WHEEL_L / 2;
            double y1 = trackY - WHEEL_W / 2;
            double y2 = trackY + WHEEL_W / 2;

            // Outer tire rectangle
            var tire = new Polyline();
            tire.AddVertexAt(0, new Point2d(x1, y1), 0, 0, 0);
            tire.AddVertexAt(1, new Point2d(x2, y1), 0, 0, 0);
            tire.AddVertexAt(2, new Point2d(x2, y2), 0, 0, 0);
            tire.AddVertexAt(3, new Point2d(x1, y2), 0, 0, 0);
            tire.Closed = true;
            tire.ColorIndex = C_WHEEL;
            tire.LineWeight = LineWeight.LineWeight025;
            btr.AppendEntity(tire);
            tx.AddNewlyCreatedDBObject(tire, true);

            // Hub center dot (small circle)
            AddCircle(btr, tx, axleX, trackY, 0.15, C_WHEEL);

            // Tread lines — two internal lines parallel to length
            double treadInset = WHEEL_W * 0.3;
            AddLine(btr, tx, x1 + 0.2, trackY + treadInset, x2 - 0.2, trackY + treadInset, C_WHEEL);
            AddLine(btr, tx, x1 + 0.2, trackY - treadInset, x2 - 0.2, trackY - treadInset, C_WHEEL);
        }

        // ═══════════════════════════════════════════════════════════
        //  Axle centerline (dashed)
        // ═══════════════════════════════════════════════════════════

        private static void DrawAxleCenterline(
            BlockTableRecord btr, Transaction tx,
            double axleX, double halfExtent)
        {
            var line = new Line(
                new Point3d(axleX, -halfExtent, 0),
                new Point3d(axleX, halfExtent, 0));
            line.ColorIndex = C_AXLE;
            line.LinetypeScale = 0.5;
            // Dashed pattern: use short segments
            btr.AppendEntity(line);
            tx.AddNewlyCreatedDBObject(line, true);

            // Small "CL" label at top of centerline
            var cl = new MText
            {
                Location = new Point3d(axleX, halfExtent + 0.4, 0),
                Contents = "CL",
                TextHeight = TEXT_H * 0.5,
                Attachment = AttachmentPoint.BottomCenter,
                ColorIndex = C_AXLE
            };
            btr.AppendEntity(cl);
            tx.AddNewlyCreatedDBObject(cl, true);
        }

        // ═══════════════════════════════════════════════════════════
        //  Side mirrors
        // ═══════════════════════════════════════════════════════════

        private static void DrawMirror(
            BlockTableRecord btr, Transaction tx,
            double x, double bodyEdgeY)
        {
            double sign = Math.Sign(bodyEdgeY);
            double outerY = bodyEdgeY + sign * (MIRROR_W + 0.3);
            DrawSmallRect(btr, tx, x - MIRROR_L / 2, outerY, MIRROR_L, MIRROR_W, C_WHEEL);
            // Mirror arm
            AddLine(btr, tx, x, bodyEdgeY, x, outerY, C_WHEEL);
        }

        // ═══════════════════════════════════════════════════════════
        //  Front direction arrow
        // ═══════════════════════════════════════════════════════════

        private static void DrawFrontArrow(
            BlockTableRecord btr, Transaction tx,
            double tipX, double centerY)
        {
            double arrowL = 2.0;
            double arrowW = 0.6;
            // Arrow shaft
            AddLine(btr, tx, tipX - arrowL, centerY, tipX, centerY, C_ARROW);
            // Arrowhead
            AddLine(btr, tx, tipX, centerY, tipX - arrowW, centerY + arrowW * 0.6, C_ARROW);
            AddLine(btr, tx, tipX, centerY, tipX - arrowW, centerY - arrowW * 0.6, C_ARROW);
            // "FRONT" label
            var label = new MText
            {
                Location = new Point3d(tipX + 0.3, centerY, 0),
                Contents = "FRONT",
                TextHeight = TEXT_H * 0.6,
                Attachment = AttachmentPoint.MiddleLeft,
                ColorIndex = C_ARROW
            };
            btr.AppendEntity(label);
            tx.AddNewlyCreatedDBObject(label, true);
        }

        // ═══════════════════════════════════════════════════════════
        //  Dimension annotations for single-unit vehicles
        // ═══════════════════════════════════════════════════════════

        private static void DrawSingleDimensions(
            BlockTableRecord btr, Transaction tx,
            VehicleUnit v, double fX, double rX, double halfW,
            double faX, double raX)
        {
            // ── Overall Length (top) ─────────────────────────────────
            double dimY1 = halfW + DIM_OFF;
            DrawHDim(btr, tx, fX, rX, dimY1, $"L = {v.Length:F1}'");

            // ── Overall Width (right) ───────────────────────────────
            double dimX1 = fX + DIM_OFF;
            DrawVDim(btr, tx, halfW, -halfW, dimX1, $"W = {v.Width:F1}'");

            // ── Wheelbase (bottom, row 1) ───────────────────────────
            double dimYb1 = -halfW - DIM_OFF;
            DrawHDim(btr, tx, faX, raX, dimYb1, $"WB = {v.Wheelbase:F1}'");

            // ── Front Overhang (bottom, row 2) ──────────────────────
            double dimYb2 = dimYb1 - DIM_SPC;
            DrawHDim(btr, tx, fX, faX, dimYb2, $"FO = {v.FrontOverhang:F1}'");

            // ── Rear Overhang (bottom, row 2) ───────────────────────
            DrawHDim(btr, tx, raX, rX, dimYb2, $"RO = {v.RearOverhang:F1}'");

            // ── Track Width text annotation (near rear axle) ────────
            var twText = new MText
            {
                Location = new Point3d(raX, -halfW - 1.5, 0),
                Contents = $"TW = {v.TrackWidth:F1}'",
                TextHeight = TEXT_H * 0.75,
                Attachment = AttachmentPoint.TopCenter,
                ColorIndex = C_DIM
            };
            btr.AppendEntity(twText);
            tx.AddNewlyCreatedDBObject(twText, true);
        }

        // ═══════════════════════════════════════════════════════════
        //  Title block — vehicle info below the drawing
        // ═══════════════════════════════════════════════════════════

        private static void DrawTitleBlock(
            BlockTableRecord btr, Transaction tx,
            string name, string symbol,
            double length, double width, double wheelbase, double minR,
            double leftX, double halfW)
        {
            double titleY = -halfW - DIM_OFF - DIM_SPC * 2 - 2.0;

            // Separator line
            AddLine(btr, tx, leftX - 2, titleY, leftX + length + 4, titleY, C_TITLE);

            // Vehicle name (large)
            var nameText = new MText
            {
                Location = new Point3d(leftX, titleY - 1.2, 0),
                Contents = $"\\W1.2;{symbol}  —  {name}",
                TextHeight = TEXT_H * 1.3,
                Attachment = AttachmentPoint.TopLeft,
                ColorIndex = C_TITLE
            };
            btr.AppendEntity(nameText);
            tx.AddNewlyCreatedDBObject(nameText, true);

            // Specs line
            var specText = new MText
            {
                Location = new Point3d(leftX, titleY - 3.0, 0),
                Contents = $"L = {length:F1}'   |   W = {width:F1}'   |   " +
                           $"WB = {wheelbase:F1}'   |   Min Turning Radius = {minR:F1}'",
                TextHeight = TEXT_H * 0.85,
                Attachment = AttachmentPoint.TopLeft,
                ColorIndex = C_DIM
            };
            btr.AppendEntity(specText);
            tx.AddNewlyCreatedDBObject(specText, true);

            // Bottom separator
            AddLine(btr, tx, leftX - 2, titleY - 4.5, leftX + length + 4, titleY - 4.5, C_TITLE);
        }

        // ═══════════════════════════════════════════════════════════
        //  Horizontal dimension helper
        // ═══════════════════════════════════════════════════════════

        private static void DrawHDim(
            BlockTableRecord btr, Transaction tx,
            double x1, double x2, double dimY, string text)
        {
            if (Math.Abs(x1 - x2) < 0.01) return;

            double left = Math.Min(x1, x2);
            double right = Math.Max(x1, x2);

            // Extension lines (vertical, from body toward dim line)
            double extStart1 = dimY > 0 ? dimY - DIM_OFF + EXT_GAP : dimY + DIM_OFF - EXT_GAP;
            AddLine(btr, tx, left, extStart1, left, dimY, C_DIM);
            AddLine(btr, tx, right, extStart1, right, dimY, C_DIM);

            // Dimension line (horizontal)
            AddLine(btr, tx, left, dimY, right, dimY, C_DIM);

            // Tick marks at ends (45° slashes)
            AddLine(btr, tx, left - TICK, dimY - TICK, left + TICK, dimY + TICK, C_DIM);
            AddLine(btr, tx, right - TICK, dimY - TICK, right + TICK, dimY + TICK, C_DIM);

            // Dimension text centered above the line
            if (!string.IsNullOrEmpty(text))
            {
                var mt = new MText
                {
                    Location = new Point3d((left + right) / 2.0, dimY + TEXT_H * 0.3, 0),
                    Contents = text,
                    TextHeight = TEXT_H,
                    Attachment = AttachmentPoint.BottomCenter,
                    ColorIndex = C_DIM
                };
                btr.AppendEntity(mt);
                tx.AddNewlyCreatedDBObject(mt, true);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Vertical dimension helper
        // ═══════════════════════════════════════════════════════════

        private static void DrawVDim(
            BlockTableRecord btr, Transaction tx,
            double y1, double y2, double dimX, string text)
        {
            if (Math.Abs(y1 - y2) < 0.01) return;

            double top = Math.Max(y1, y2);
            double bot = Math.Min(y1, y2);

            // Extension lines (horizontal, from body toward dim line)
            double extStart = dimX - DIM_OFF + EXT_GAP;
            AddLine(btr, tx, extStart, top, dimX, top, C_DIM);
            AddLine(btr, tx, extStart, bot, dimX, bot, C_DIM);

            // Dimension line (vertical)
            AddLine(btr, tx, dimX, bot, dimX, top, C_DIM);

            // Tick marks
            AddLine(btr, tx, dimX - TICK, top - TICK, dimX + TICK, top + TICK, C_DIM);
            AddLine(btr, tx, dimX - TICK, bot - TICK, dimX + TICK, bot + TICK, C_DIM);

            // Dimension text to the right, rotated 90°
            if (!string.IsNullOrEmpty(text))
            {
                var mt = new MText
                {
                    Location = new Point3d(dimX + TEXT_H * 0.3, (top + bot) / 2.0, 0),
                    Contents = text,
                    TextHeight = TEXT_H,
                    Attachment = AttachmentPoint.BottomCenter,
                    ColorIndex = C_DIM,
                    Rotation = Math.PI / 2.0
                };
                btr.AppendEntity(mt);
                tx.AddNewlyCreatedDBObject(mt, true);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Primitive helpers
        // ═══════════════════════════════════════════════════════════

        private static void AddLine(
            BlockTableRecord btr, Transaction tx,
            double x1, double y1, double x2, double y2, short color)
        {
            var line = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0))
            {
                ColorIndex = color
            };
            btr.AppendEntity(line);
            tx.AddNewlyCreatedDBObject(line, true);
        }

        private static void AddCircle(
            BlockTableRecord btr, Transaction tx,
            double cx, double cy, double radius, short color)
        {
            var circle = new Circle(new Point3d(cx, cy, 0), Vector3d.ZAxis, radius)
            {
                ColorIndex = color
            };
            btr.AppendEntity(circle);
            tx.AddNewlyCreatedDBObject(circle, true);
        }

        private static void DrawSmallRect(
            BlockTableRecord btr, Transaction tx,
            double cx, double cy, double w, double h, short color)
        {
            double hw = w / 2.0, hh = h / 2.0;
            var rect = new Polyline();
            rect.AddVertexAt(0, new Point2d(cx - hw, cy - hh), 0, 0, 0);
            rect.AddVertexAt(1, new Point2d(cx + hw, cy - hh), 0, 0, 0);
            rect.AddVertexAt(2, new Point2d(cx + hw, cy + hh), 0, 0, 0);
            rect.AddVertexAt(3, new Point2d(cx - hw, cy + hh), 0, 0, 0);
            rect.Closed = true;
            rect.ColorIndex = color;
            btr.AppendEntity(rect);
            tx.AddNewlyCreatedDBObject(rect, true);
        }
    }
}
