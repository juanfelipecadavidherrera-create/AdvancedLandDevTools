using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Autodesk.Windows;

namespace AdvancedLandDevTools.Ribbon
{
    /// <summary>
    /// Builds the "Advanced Land Dev Tools" ribbon tab with a Profiles panel
    /// containing the Bulk Surface Profile button.
    ///
    /// Called once from AppLoader.Initialize().
    /// </summary>
    public static class RibbonBuilder
    {
        private const string TAB_ID                  = "ALDT_TAB_2026";
        private const string PANEL_PROFILEVIEWS_ID   = "ALDT_PANEL_PROFILEVIEWS";
        private const string PANEL_ALIGNMENTS_ID     = "ALDT_PANEL_ALIGNMENTS";
        private const string PANEL_PIPES_ID          = "ALDT_PANEL_PIPES";
        private const string PANEL_FLOODZONE_ID      = "ALDT_PANEL_FLOODZONE";

        // ─────────────────────────────────────────────────────────────────────
        public static void BuildRibbon()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            // Guard: don't add the tab twice (e.g. NETLOAD called twice)
            foreach (RibbonTab existing in ribbon.Tabs)
            {
                if (existing.Id == TAB_ID) return;
            }

            // ── Create the tab ────────────────────────────────────────────────
            var tab = new RibbonTab
            {
                Id    = TAB_ID,
                Title = "Advanced Land Dev Tools",
                IsActive = false
            };

            // ══════════════════════════════════════════════════════════════════
            //  Panel 1 — Profile Views
            // ══════════════════════════════════════════════════════════════════
            var pvSource = new RibbonPanelSource
            {
                Id    = PANEL_PROFILEVIEWS_ID,
                Title = "Profile Views"
            };

            // ── Bulk Surface Profile button ───────────────────────────────────
            var btnBulkSur = new RibbonButton
            {
                Id               = "ALDT_BTN_BULKSUR",
                Name             = "Bulk Surface Profile",
                Text             = "Bulk Surface\nProfile",
                Description      = "Creates surface profiles and profile views for multiple " +
                                   "alignments in a single operation.",
                ToolTip          = BuildToolTip(
                    "Bulk Surface Profile Creator",
                    "Batch-creates surface profiles and profile views for any number " +
                    "of centerline alignments.\n\nCommand:  BULKSUR"),
                CommandHandler   = new RibbonCommandHandler("BULKSUR "),
                CommandParameter = "BULKSUR ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildBulkSurfaceIcon(32),
                Image            = BuildBulkSurfaceIcon(16)
            };
            pvSource.Items.Add(btnBulkSur);

            pvSource.Items.Add(new RibbonSeparator());

            // ── Get Parent Alignment button ───────────────────────────────────
            var btnGetParent = new RibbonButton
            {
                Id               = "ALDT_BTN_GETPARENT",
                Name             = "Get Parent Alignment",
                Text             = "Get Parent\nAlignment",
                Description      = "Selects the parent alignment of a picked profile view " +
                                   "and reports its station range.",
                ToolTip          = BuildToolTip(
                    "Get Parent Alignment",
                    "Pick any profile view in the drawing. The tool identifies its " +
                    "parent alignment, selects it, and reports the name and station " +
                    "range in the command line.\n\nCommand:  GETPARENT"),
                CommandHandler   = new RibbonCommandHandler("GETPARENT "),
                CommandParameter = "GETPARENT ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildGetParentIcon(32),
                Image            = BuildGetParentIcon(16)
            };
            pvSource.Items.Add(btnGetParent);

            pvSource.Items.Add(new RibbonSeparator());

            // ── Pipe Magic button ─────────────────────────────────────────────
            var btnPipeMagic = new RibbonButton
            {
                Id               = "ALDT_BTN_PIPEMAGIC",
                Name             = "Pipe Magic",
                Text             = "Pipe\nMagic",
                Description      = "Automatically detects all crossing pipe networks " +
                                   "(gravity and pressure) and projects them into selected " +
                                   "profile views.",
                ToolTip          = BuildToolTip(
                    "Pipe Magic",
                    "Select one or more profile views. The tool finds every gravity " +
                    "and pressure pipe network that crosses the alignment and projects " +
                    "them into each profile view automatically.\n\nCommand:  PIPEMAGIC"),
                CommandHandler   = new RibbonCommandHandler("PIPEMAGIC "),
                CommandParameter = "PIPEMAGIC ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildPipeMagicIcon(32),
                Image            = BuildPipeMagicIcon(16)
            };
            pvSource.Items.Add(btnPipeMagic);

            pvSource.Items.Add(new RibbonSeparator());

            // ── Label Gen button ──────────────────────────────────────────────
            var btnLLabelGen = new RibbonButton
            {
                Id               = "ALDT_BTN_LLABELGEN",
                Name             = "Label Gen",
                Text             = "Label\nGen",
                Description      = "Automatically places 3D Profile View Station Elevation Labels " +
                                   "at pipe crossing inverts.",
                ToolTip          = BuildToolTip(
                    "Label Generator",
                    "Select a profile view (native or XREF). The tool detects crossing pipes, " +
                    "filters by network, and places formatted Station Elevation Labels at the pipe inverts.\n\nCommand:  LLABELGEN"),
                CommandHandler   = new RibbonCommandHandler("LLABELGEN "),
                CommandParameter = "LLABELGEN ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildLLabelGenIcon(32),
                Image            = BuildLLabelGenIcon(16)
            };
            pvSource.Items.Add(btnLLabelGen);

            pvSource.Items.Add(new RibbonSeparator());

            // ── Mark Lines button ───────────────────────────────────────────
            var btnMarkLines = new RibbonButton
            {
                Id               = "ALDT_BTN_MARKLINES",
                Name             = "Mark Lines",
                Text             = "Mark\nLines",
                Description      = "Draws vertical marker lines in a profile view at each " +
                                   "station where lines on selected layers cross the alignment.",
                ToolTip          = BuildToolTip(
                    "Mark Lines",
                    "Select layers, then pick a profile view. The tool finds every line " +
                    "and polyline on those layers that crosses the profile view's alignment " +
                    "and draws a full-height vertical line at each crossing station.\n\n" +
                    "Command:  MARKLINES"),
                CommandHandler   = new RibbonCommandHandler("MARKLINES "),
                CommandParameter = "MARKLINES ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildMarkLinesIcon(32),
                Image            = BuildMarkLinesIcon(16)
            };
            pvSource.Items.Add(btnMarkLines);

            pvSource.Items.Add(new RibbonSeparator());

            // ── Mark Fittings button ───────────────────────────────────────────
            var btnMarkFittings = new RibbonButton
            {
                Id               = "ALDT_BTN_MARKFITTINGS",
                Name             = "Mark Fittings",
                Text             = "Mark\nFittings",
                Description      = "Draws dashed vertical marker lines in a profile view for pressure fittings.",
                ToolTip          = BuildToolTip(
                    "Mark Fittings",
                    "Select a profile view, and the tool will automatically detect pressure fittings within it " +
                    "and draw dashed vertical marker lines from top to bottom.\n\n" +
                    "Command:  MARKFITTINGS"),
                CommandHandler   = new RibbonCommandHandler("MARKFITTINGS "),
                CommandParameter = "MARKFITTINGS ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildMarkFittingsIcon(32),
                Image            = BuildMarkFittingsIcon(16)
            };
            pvSource.Items.Add(btnMarkFittings);

            // ── Profile Off button ────────────────────────────────────────────
            var btnProfOff = new RibbonButton
            {
                Id               = "ALDT_BTN_PROFOFF",
                Name             = "Profile Off",
                Text             = "Profile\nOff",
                Description      = "Remove pipes and structures from a Civil 3D profile view. " +
                                   "Pick the profile view, then click each part to hide.",
                ToolTip          = BuildToolTip(
                    "Profile Off",
                    "Step 1: click the profile view border. " +
                    "Step 2: click each pipe or structure crossing to hide it. " +
                    "Works with gravity pipes, structures, and pressure parts.\n\nCommand: PROFOFF"),
                CommandHandler   = new RibbonCommandHandler("PROFOFF "),
                CommandParameter = "PROFOFF ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildProfOffIcon(32),
                Image            = BuildProfOffIcon(16)
            };
            pvSource.Items.Add(btnProfOff);

            pvSource.Items.Add(new RibbonSeparator());

            // ── PV Style Override button ──────────────────────────────────────
            var btnPvStyle = new RibbonButton
            {
                Id               = "ALDT_BTN_PVSTYLE",
                Name             = "PV Style Override",
                Text             = "PV Style\nOverride",
                Description      = "Change the per-profile-view style override for a pipe or structure. " +
                                   "Does not affect the global pipe style.",
                ToolTip          = BuildToolTip(
                    "PV Style Override",
                    "Step 1: click the profile view border. " +
                    "Step 2: click a pipe or structure, then pick a style from the list. " +
                    "Sets the Style Override column in Profile View Properties.\n\nCommand: PVSTYLE"),
                CommandHandler   = new RibbonCommandHandler("PVSTYLE "),
                CommandParameter = "PVSTYLE ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildPvStyleIcon(32),
                Image            = BuildPvStyleIcon(16)
            };
            pvSource.Items.Add(btnPvStyle);

            pvSource.Items.Add(new RibbonSeparator());

            // ── ChopChop button ───────────────────────────────────────────────
            var btnChopChop = new RibbonButton
            {
                Id               = "ALDT_BTN_CHOPCHOP",
                Name             = "ChopChop",
                Text             = "Chop\nChop",
                Description      = "Subdivide a profile view into smaller views along the " +
                                   "station axis. Equal or custom intervals.",
                ToolTip          = BuildToolTip(
                    "ChopChop — Profile View Subdivider",
                    "Select a profile view, choose Equal or Custom subdivision, and the " +
                    "tool creates smaller profile views side by side below the original. " +
                    "Styles and elevation ranges are copied from the source.\n\nCommand:  CHOPCHOP"),
                CommandHandler   = new RibbonCommandHandler("CHOPCHOP "),
                CommandParameter = "CHOPCHOP ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildChopChopIcon(32),
                Image            = BuildChopChopIcon(16)
            };
            pvSource.Items.Add(btnChopChop);

            tab.Panels.Add(new RibbonPanel { Source = pvSource });

            // ══════════════════════════════════════════════════════════════════
            //  Panel 2 — Alignments
            // ══════════════════════════════════════════════════════════════════
            var alSource = new RibbonPanelSource
            {
                Id    = PANEL_ALIGNMENTS_ID,
                Title = "Alignments"
            };

            // ── Align Deploy button ───────────────────────────────────────────
            var btnAlignDeploy = new RibbonButton
            {
                Id               = "ALDT_BTN_ALIGNDEPLOY",
                Name             = "Align Deploy",
                Text             = "Align\nDeploy",
                Description      = "Deploys copies of a cross alignment along a main alignment " +
                                   "at a specified interval.",
                ToolTip          = BuildToolTip(
                    "Align Deploy",
                    "Select a main (long) alignment and a cross (short) alignment. " +
                    "Copies of the cross alignment are placed at a set interval " +
                    "along the main alignment.\n\nCommand:  ALIGNDEPLOY"),
                CommandHandler   = new RibbonCommandHandler("ALIGNDEPLOY "),
                CommandParameter = "ALIGNDEPLOY ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildAlignDeployIcon(32),
                Image            = BuildAlignDeployIcon(16)
            };
            alSource.Items.Add(btnAlignDeploy);

            tab.Panels.Add(new RibbonPanel { Source = alSource });

            // ══════════════════════════════════════════════════════════════════
            //  Panel 3 — Pipes
            // ══════════════════════════════════════════════════════════════════
            var piSource = new RibbonPanelSource
            {
                Id    = PANEL_PIPES_ID,
                Title = "Pipes"
            };

            // ── Invert Pull Up button ─────────────────────────────────────────
            var btnInvertPullUp = new RibbonButton
            {
                Id               = "ALDT_BTN_INVERTPULLUP",
                Name             = "Invert Pull Up",
                Text             = "Invert\nPull Up",
                Description      = "Click any pipe (gravity or pressure), then click a point " +
                                   "on that pipe to calculate the invert elevation at that location.",
                ToolTip          = BuildToolTip(
                    "Invert Pull Up",
                    "Select a gravity or pressure pipe, then pick any point along it. " +
                    "Calculates the exact invert elevation at that location via linear " +
                    "interpolation between start and end inverts.\n\nCommand: INVERTPULLUP"),
                CommandHandler   = new RibbonCommandHandler("INVERTPULLUP "),
                CommandParameter = "INVERTPULLUP ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildInvertPullUpIcon(32),
                Image            = BuildInvertPullUpIcon(16)
            };
            piSource.Items.Add(btnInvertPullUp);

            piSource.Items.Add(new RibbonSeparator());

            // ── Change Elevation button ─────────────────────────────────────────
            var btnChangeElev = new RibbonButton
            {
                Id               = "ALDT_BTN_CHANGEELEVATION",
                Name             = "Change Elevation",
                Text             = "Change\nElevation",
                Description      = "Select a pipe to equalize its start and end invert elevations.",
                ToolTip          = BuildToolTip(
                    "Change Elevation",
                    "Select a gravity or pressure pipe. The tool shows both start and " +
                    "end invert elevations and lets you choose which one to apply to " +
                    "both ends, making the pipe level.\n\nCommand:  CHANGEELEVATION"),
                CommandHandler   = new RibbonCommandHandler("CHANGEELEVATION "),
                CommandParameter = "CHANGEELEVATION ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildChangeElevationIcon(32),
                Image            = BuildChangeElevationIcon(16)
            };
            piSource.Items.Add(btnChangeElev);

            piSource.Items.Add(new RibbonSeparator());

            // ── Pipe Sizing button ────────────────────────────────────────────
            var btnPipeSizing = new RibbonButton
            {
                Id               = "ALDT_BTN_PIPESIZING",
                Name             = "Pipe Sizing",
                Text             = "Pipe\nSizing",
                Description      = "Rational Method + Manning's Equation pipe sizing calculator. " +
                                   "Check pipe capacity and velocity for a given diameter and slope.",
                ToolTip          = BuildToolTip(
                    "Pipe Sizing Calculator",
                    "Calculates stormwater runoff (Rational Method: Q = CiA) and " +
                    "pipe flow capacity (Manning's Equation). Checks if the selected " +
                    "pipe diameter passes capacity and velocity criteria.\n\nCommand: PIPESIZING"),
                CommandHandler   = new RibbonCommandHandler("PIPESIZING "),
                CommandParameter = "PIPESIZING ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildPipeSizingIcon(32),
                Image            = BuildPipeSizingIcon(16)
            };
            piSource.Items.Add(btnPipeSizing);

            piSource.Items.Add(new RibbonSeparator());

            // ── EEE Bend (Duck) button ────────────────────────────────────────
            var btnEeeBend = new RibbonButton
            {
                Id               = "ALDT_BTN_EEEBEND",
                Name             = "EEE Bend",
                Text             = "EEE\nBend",
                Description      = "Inserts a pressure-network pipe duck around a crossing pipe " +
                                   "in a profile view. Adds 4 bends and 5 pipe segments.",
                ToolTip          = BuildToolTip(
                    "EEE Bend — Pressure Pipe Duck",
                    "Select a pressure pipe run in a profile, click the crossing location, " +
                    "enter the crossing invert elevation. The command places 4 bends to " +
                    "route the pipe under the crossing: ±10 ft horizontal offset, " +
                    "then 11.6 ft diagonals at 1H:10V slope.\n\nCommand: EEEBEND"),
                CommandHandler   = new RibbonCommandHandler("EEEBEND "),
                CommandParameter = "EEEBEND ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildEeeBendIcon(32),
                Image            = BuildEeeBendIcon(16)
            };
            piSource.Items.Add(btnEeeBend);

            piSource.Items.Add(new RibbonSeparator());

            // ── Cover Adjust button ───────────────────────────────────────────
            var btnCoverAdjust = new RibbonButton
            {
                Id               = "ALDT_BTN_COVERADJUST",
                Name             = "Cover Adjust",
                Text             = "Cover\nAdjust",
                Description      = "Selects two pressure fittings in a profile view and adjusts " +
                                   "both PVIs to the shallowest depth that gives each fitting " +
                                   "≥ 4 ft of crown cover, placing both at the same elevation.",
                ToolTip          = BuildToolTip(
                    "Cover Adjust",
                    "Click two pressure fitting proxies in a profile view. The tool " +
                    "auto-detects the ground-surface elevation from the alignment's " +
                    "surface profile, computes the minimum depth that gives both fittings " +
                    "≥ 4 ft of crown cover, and moves both PVIs to that elevation so they " +
                    "sit at the same height.\n\nCommand:  COVERADJUST"),
                CommandHandler   = new RibbonCommandHandler("COVERADJUST "),
                CommandParameter = "COVERADJUST ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildCoverAdjustIcon(32),
                Image            = BuildCoverAdjustIcon(16)
            };
            piSource.Items.Add(btnCoverAdjust);

            piSource.Items.Add(new RibbonSeparator());

            // ── Pressure Count button ─────────────────────────────────────────
            var btnPressCount = new RibbonButton
            {
                Id               = "ALDT_BTN_PRESSCOUNT",
                Name             = "Pressure Count",
                Text             = "Pressure\nCount",
                Description      = "Select a Civil 3D pressure network to compute total 3D pipe " +
                                   "length, number all fittings, and optionally place MText labels.",
                ToolTip          = BuildToolTip(
                    "Pressure Network Count",
                    "Scans the drawing for pressure networks, lets you pick one, then " +
                    "calculates the total 3D pipe length and assigns sequential numbers " +
                    "to every fitting. Optionally places numbered MText labels at each " +
                    "fitting location and groups them into a single AutoCAD Group.\n\nCommand: PRESSCOUNT"),
                CommandHandler   = new RibbonCommandHandler("PRESSCOUNT "),
                CommandParameter = "PRESSCOUNT ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildPressCountIcon(32),
                Image            = BuildPressCountIcon(16)
            };
            piSource.Items.Add(btnPressCount);

            piSource.Items.Add(new RibbonSeparator());

            // ── RR Network Check button ─────────────────────────────────────────
            var btnRrNet = new RibbonButton
            {
                Id               = "ALDT_BTN_RRNETWORKCHECK",
                Name             = "RR Network Check",
                Text             = "Network\nCheck",
                Description      = "Checks clearance and cover between pressure pipe parts " +
                                   "and a surface in a profile view.",
                ToolTip          = BuildToolTip(
                    "Pressure Network Clearance Check",
                    "Click a pressure pipe part in a profile view. The tool auto-detects " +
                    "the surface, checks minimum clearance and cover at every point, and " +
                    "reports pass/fail results.\n\nCommand:  RRNETWORKCHECK"),
                CommandHandler   = new RibbonCommandHandler("RRNETWORKCHECK "),
                CommandParameter = "RRNETWORKCHECK ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildRrNetworkCheckIcon(32),
                Image            = BuildRrNetworkCheckIcon(16)
            };
            piSource.Items.Add(btnRrNet);

            piSource.Items.Add(new RibbonSeparator());

            // ── Lateral Manager button ─────────────────────────────────────────
            var btnLatMan = new RibbonButton
            {
                Id               = "ALDT_BTN_LATERALMANAGER",
                Name             = "Lateral Manager",
                Text             = "Lateral\nManager",
                Description      = "Manage and project 2D sewer lateral crossings into profile views.",
                ToolTip          = BuildToolTip(
                    "Lateral Crossing Manager",
                    "A dockable palette to track 2D sewer laterals. Add drawn ellipses " +
                    "from lateral profile views and automatically project them accurately " +
                    "into target profile views (e.g. water main).\n\nCommand:  LATMANAGER"),
                CommandHandler   = new RibbonCommandHandler("LATMANAGER "),
                CommandParameter = "LATMANAGER ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildLatManIcon(32),
                Image            = BuildLatManIcon(16)
            };
            piSource.Items.Add(btnLatMan);

            tab.Panels.Add(new RibbonPanel { Source = piSource });

            // ══════════════════════════════════════════════════════════════════
            //  Panel 4 — Surfaces
            // ══════════════════════════════════════════════════════════════════
            var sfSource = new RibbonPanelSource
            {
                Id    = "ALDT_PANEL_SURFACES",
                Title = "Surfaces"
            };

            // ── Elev Slope button ─────────────────────────────────────────────
            var btnElevSlope = new RibbonButton
            {
                Id               = "ALDT_BTN_ELEVSLOPE",
                Name             = "Elev Slope",
                Text             = "Elev\nSlope",
                Description      = "Pick a point on a TIN surface, set a slope, and click " +
                                   "to add a new surface point at the calculated elevation.",
                ToolTip          = BuildToolTip(
                    "Elevation Sloper",
                    "Select a TIN surface, pick a start point to get its elevation. " +
                    "Set a slope (default 2%). Click a new location to add a surface " +
                    "point whose elevation is calculated from the horizontal distance " +
                    "and slope.\n\nCommand: ELEVSLOPE"),
                CommandHandler   = new RibbonCommandHandler("ELEVSLOPE "),
                CommandParameter = "ELEVSLOPE ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildElevSlopeIcon(32),
                Image            = BuildElevSlopeIcon(16)
            };
            sfSource.Items.Add(btnElevSlope);

            sfSource.Items.Add(new RibbonSeparator());

            // ── Low Rim button ────────────────────────────────────────────────
            var btnLowRim = new RibbonButton
            {
                Id               = "ALDT_BTN_LOWRIM",
                Name             = "Low Rim",
                Text             = "Low\nRim",
                Description      = "Find the structure with the lowest surface elevation " +
                                   "at its insertion point in a pipe network.",
                ToolTip          = BuildToolTip(
                    "Lowest Rim Elevation",
                    "Select a gravity pipe network. Scans all structures and " +
                    "reports the one with the lowest Surface Elevation at " +
                    "Insertion Point.\n\nCommand: LOWRIM"),
                CommandHandler   = new RibbonCommandHandler("LOWRIM "),
                CommandParameter = "LOWRIM ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildLowRimIcon(32),
                Image            = BuildLowRimIcon(16)
            };
            sfSource.Items.Add(btnLowRim);

            sfSource.Items.Add(new RibbonSeparator());

            // ── Block to Surface button ──────────────────────────────────
            var btnB2S = new RibbonButton
            {
                Id               = "ALDT_BTN_BLOCKTOSURFACE",
                Name             = "Block to Surface",
                Text             = "Block to\nSurface",
                Description      = "Read ELEV2 attribute from block references and add " +
                                   "elevation points to a TIN surface at each block location.",
                ToolTip          = BuildToolTip(
                    "Block to Surface",
                    "Select a TIN surface and a block reference. Finds all instances " +
                    "of that block, reads their ELEV2 attribute for elevation, and " +
                    "adds a surface point at each block's coordinates.\n\nCommand: BLOCKTOSURFACE"),
                CommandHandler   = new RibbonCommandHandler("BLOCKTOSURFACE "),
                CommandParameter = "BLOCKTOSURFACE ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildBlockToSurfaceIcon(32),
                Image            = BuildBlockToSurfaceIcon(16)
            };
            sfSource.Items.Add(btnB2S);

            // ── Text to Surface button ────────────────────────────────────
            var btnT2S = new RibbonButton
            {
                Id               = "ALDT_BTN_TEXTTOSURFACE",
                Name             = "Text to Surface",
                Text             = "Text to\nSurface",
                Description      = "Read elevation numbers from MTexts and MLeaders and " +
                                   "add surface points to a TIN surface.",
                ToolTip          = BuildToolTip(
                    "Text to Surface",
                    "Select MTexts and/or MLeaders that contain elevation numbers. " +
                    "Parses the text content (handles '7.62\\' format), places the point " +
                    "at the MText location or the MLeader arrowhead tip, and adds it to " +
                    "the chosen TIN surface.\n\nCommand: TEXTTOSURFACE"),
                CommandHandler   = new RibbonCommandHandler("TEXTTOSURFACE "),
                CommandParameter = "TEXTTOSURFACE ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildTextToSurfaceIcon(32),
                Image            = BuildTextToSurfaceIcon(16)
            };
            sfSource.Items.Add(btnT2S);

            tab.Panels.Add(new RibbonPanel { Source = sfSource });

            // ══════════════════════════════════════════════════════════════════
            //  Panel 5 — Flood Zone
            // ══════════════════════════════════════════════════════════════════
            var fzSource = new RibbonPanelSource
            {
                Id    = PANEL_FLOODZONE_ID,
                Title = "Property Information"
            };

            // ── Property Appraiser Lookup button ────────────────────────────────────
            var btnPropertyAppraisal = new RibbonButton
            {
                Id               = "ALDT_BTN_PROPERTYAPPRAISAL",
                Name             = "Property Appraiser",
                Text             = "Property\nAppraiser",
                Description      = "Click a point in the drawing to query Miami-Dade Property Appraiser " +
                                   "for folio, owner, and address.",
                ToolTip          = BuildToolTip(
                    "Property Appraiser Lookup",
                    "Pick a point in the drawing. The tool queries MDC GIS to " +
                    "retrieve property information including folio, owner name, and address.\n\nCommand:  FOLIO"),
                CommandHandler   = new RibbonCommandHandler("FOLIO "),
                CommandParameter = "FOLIO ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildPropertyAppraisalIcon(32),
                Image            = BuildPropertyAppraisalIcon(16)
            };
            fzSource.Items.Add(btnPropertyAppraisal);

            fzSource.Items.Add(new RibbonSeparator());

            // ── Flood Zone Lookup button ────────────────────────────────────
            var btnFloodZone = new RibbonButton
            {
                Id               = "ALDT_BTN_FLOODZONE",
                Name             = "Flood Zone (FEMA)",
                Text             = "Flood Zone\n(FEMA)",
                Description      = "Click a point in the drawing to query FEMA NFHL " +
                                   "for flood zone designation, BFE, and SFHA status.",
                ToolTip          = BuildToolTip(
                    "Flood Zone Lookup",
                    "Pick a point in the drawing. The tool queries the FEMA National " +
                    "Flood Hazard Layer and reports the flood zone, base flood elevation, " +
                    "and SFHA status. Optionally places an MText label.\n\nCommand:  FLOODZONE"),
                CommandHandler   = new RibbonCommandHandler("FLOODZONE "),
                CommandParameter = "FLOODZONE ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildFloodZoneIcon(32),
                Image            = BuildFloodZoneIcon(16)
            };
            fzSource.Items.Add(btnFloodZone);

            fzSource.Items.Add(new RibbonSeparator());

            // ── County Flood Criteria button ────────────────────────────────
            var btnFloodCriteria = new RibbonButton
            {
                Id               = "ALDT_BTN_FLOODCRITERIA",
                Name             = "County Flood",
                Text             = "County\nFlood",
                Description      = "Click a point in the drawing to query Miami-Dade County " +
                                   "Flood Criteria 2022 for the minimum finished floor elevation.",
                ToolTip          = BuildToolTip(
                    "County Flood Criteria",
                    "Pick a point in the drawing. The tool queries the MDC Flood Criteria " +
                    "2022 dataset and reports the nearest flood criteria elevation contour. " +
                    "Results shown in command line only.\n\nCommand:  FLOODCRITERIA"),
                CommandHandler   = new RibbonCommandHandler("FLOODCRITERIA "),
                CommandParameter = "FLOODCRITERIA ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildFloodCriteriaIcon(32),
                Image            = BuildFloodCriteriaIcon(16)
            };
            fzSource.Items.Add(btnFloodCriteria);

            fzSource.Items.Add(new RibbonSeparator());

            // ── Section Lookup (TTRRSS) button ────────────────────────────
            var btnSectionLookup = new RibbonButton
            {
                Id               = "ALDT_BTN_SECTIONLOOKUP",
                Name             = "Section Lookup",
                Text             = "SSTTRR\nLookup",
                Description      = "Click a point in the drawing to query the PLSS " +
                                   "Township/Range/Section (TTRRSS) designation.",
                ToolTip          = BuildToolTip(
                    "Section Lookup (TTRRSS)",
                    "Pick a point in the drawing. The tool queries the SFWMD PLSS " +
                    "service and reports the Section, Township, Range, and combined " +
                    "SSTTRR code.\n\nCommand:  SECTIONLOOKUP"),
                CommandHandler   = new RibbonCommandHandler("SECTIONLOOKUP "),
                CommandParameter = "SECTIONLOOKUP ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildSectionLookupIcon(32),
                Image            = BuildSectionLookupIcon(16)
            };
            fzSource.Items.Add(btnSectionLookup);

            fzSource.Items.Add(new RibbonSeparator());

            // ── Water Table — Avg May button ─────────────────────────────
            var btnGwMay = new RibbonButton
            {
                Id               = "ALDT_BTN_GWMAY",
                Name             = "Water Table May",
                Text             = "Water Table\nMay",
                Description      = "Click a point to query the MDC Average May Groundwater " +
                                   "Level contours. Returns interpolated elevation in NAVD 88 and NGVD 29.",
                ToolTip          = BuildToolTip(
                    "Water Table — Avg May",
                    "Pick a point in the drawing. The tool queries the Miami-Dade County " +
                    "Average May Groundwater Level contour dataset, interpolates between " +
                    "the two nearest contours, and reports the elevation in both NAVD 88 " +
                    "and NGVD 29.\n\nCommand:  GWMAY"),
                CommandHandler   = new RibbonCommandHandler("GWMAY "),
                CommandParameter = "GWMAY ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildGroundwaterIcon(32),
                Image            = BuildGroundwaterIcon(16)
            };
            fzSource.Items.Add(btnGwMay);

            fzSource.Items.Add(new RibbonSeparator());

            // ── Water Table — October 2040 button ────────────────────────
            var btnGwOct = new RibbonButton
            {
                Id               = "ALDT_BTN_GWOCT",
                Name             = "Water Table Oct",
                Text             = "Water Table\nOct",
                Description      = "Click a point to query the MDC October 2040 Groundwater " +
                                   "Level raster. Returns elevation in NAVD 88 and NGVD 29.",
                ToolTip          = BuildToolTip(
                    "Water Table — October 2040",
                    "Pick a point in the drawing. The tool queries the Miami-Dade County " +
                    "Groundwater Level dataset (USGS model, October 2040 projection) and " +
                    "reports the elevation in both NAVD 88 and NGVD 29.\n\nCommand:  GWOCT"),
                CommandHandler   = new RibbonCommandHandler("GWOCT "),
                CommandParameter = "GWOCT ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildGroundwaterIcon(32),
                Image            = BuildGroundwaterIcon(16)
            };
            fzSource.Items.Add(btnGwOct);

            tab.Panels.Add(new RibbonPanel { Source = fzSource });

            // ══════════════════════════════════════════════════════════════════
            //  Panel 6 — Viewports
            // ══════════════════════════════════════════════════════════════════
            var vpSource = new RibbonPanelSource
            {
                Id    = "ALDT_PANEL_VIEWPORTS",
                Title = "Viewports"
            };

            var btnVpCut = new RibbonButton
            {
                Id               = "ALDT_BTN_VPCUT",
                Name             = "VP Cut",
                Text             = "VP\nCut",
                Description      = "Cut a viewport into smaller clipped viewports using " +
                                   "closed shapes drawn in model space.",
                ToolTip          = BuildToolTip(
                    "Viewport Cut",
                    "Select a viewport, then select closed shapes (polylines, circles, " +
                    "rectangles) inside model space. Each shape becomes a new clipped " +
                    "viewport with the same scale and properties. The original viewport " +
                    "is deleted.\n\nCommand:  VPCUT"),
                CommandHandler   = new RibbonCommandHandler("VPCUT "),
                CommandParameter = "VPCUT ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildVpCutIcon(32),
                Image            = BuildVpCutIcon(16)
            };
            vpSource.Items.Add(btnVpCut);

            tab.Panels.Add(new RibbonPanel { Source = vpSource });

            // ══════════════════════════════════════════════════════════════════
            //  Panel 7 — Vehicle Tracking
            // ══════════════════════════════════════════════════════════════════
            var vtSource = new RibbonPanelSource
            {
                Id    = "ALDT_PANEL_VEHICLETRACKING",
                Title = "Vehicle Tracking"
            };

            var btnVtDrive = new RibbonButton
            {
                Id               = "ALDT_BTN_VTDRIVE",
                Name             = "Drive",
                Text             = "Interactive\nDrive",
                Description      = "Interactively drive a vehicle with the mouse, seeing the " +
                                   "swept path build in real-time.",
                ToolTip          = BuildToolTip(
                    "Interactive Drive Mode",
                    "Place a vehicle on the drawing and drive it interactively " +
                    "using the mouse. The vehicle body, wheel tracks, and swept " +
                    "envelope are drawn in real-time as you steer.\n\nCommand: VTDRIVE"),
                CommandHandler   = new RibbonCommandHandler("VTDRIVE "),
                CommandParameter = "VTDRIVE ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildVtSweepIcon(32),
                Image            = BuildVtSweepIcon(16)
            };
            vtSource.Items.Add(btnVtDrive);

            tab.Panels.Add(new RibbonPanel { Source = vtSource });

            // ══════════════════════════════════════════════════════════════════
            //  Panel 8 — Quick Access (Mini Toolbar toggle)
            // ══════════════════════════════════════════════════════════════════
            var qaSource = new RibbonPanelSource
            {
                Id    = "ALDT_PANEL_QUICKACCESS",
                Title = "Quick Access"
            };

            var btnMiniToolbar = new RibbonButton
            {
                Id               = "ALDT_BTN_MINITOOLBAR",
                Name             = "Mini Toolbar",
                Text             = "Mini\nToolbar",
                Description      = "Toggle the floating ALDT mini toolbar on/off. " +
                                   "The mini toolbar provides quick access to all commands " +
                                   "from any ribbon tab.",
                ToolTip          = BuildToolTip(
                    "Mini Toolbar",
                    "Show or hide a compact floating toolbar with shortcuts to all " +
                    "ALDT commands. The toolbar stays on top of the drawing area so you " +
                    "can access commands from any ribbon tab.\n\nCommand:  ALDTTOOLBAR"),
                CommandHandler   = new RibbonCommandHandler("ALDTTOOLBAR "),
                CommandParameter = "ALDTTOOLBAR ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildMiniToolbarIcon(32),
                Image            = BuildMiniToolbarIcon(16)
            };
            qaSource.Items.Add(btnMiniToolbar);



            // ── Area Manager button ────────────────────────────────────────
            var btnAreaManager = new RibbonButton
            {
                Id               = "ALDT_BTN_AREAMANAGER",
                Name             = "Area Manager",
                Text             = "Area\nManager",
                Description      = "Open the Area Manager palette to track, store, and " +
                                   "export hatch/boundary areas across your project.",
                ToolTip          = BuildToolTip(
                    "Area Manager",
                    "Toggle the Area Manager dockable panel. Store hatch and boundary " +
                    "areas, organize by category, redraw deleted hatches, and export " +
                    "area reports to CSV.\n\nCommand:  AREAMANAGER"),
                CommandHandler   = new RibbonCommandHandler("AREAMANAGER "),
                CommandParameter = "AREAMANAGER ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildAreaManagerIcon(32),
                Image            = BuildAreaManagerIcon(16)
            };
            qaSource.Items.Add(btnAreaManager);

            // ── EXF Trench Manager button ─────────────────────────────────
            var btnTrenchManager = new RibbonButton
            {
                Id               = "ALDT_BTN_TRENCHMANAGER",
                Name             = "EXF Trench Manager",
                Text             = "Trench\nManager",
                Description      = "Open the EXF Trench Manager palette to track linear " +
                                   "trench lengths per project.",
                ToolTip          = BuildToolTip(
                    "EXF Trench Manager",
                    "Toggle the EXF Trench Manager dockable panel. Select a polyline and " +
                    "the tool stores the longest consecutive segment (e.g. longest side " +
                    "of a rectangle). Zoom to any stored trench.\n\nCommand: EXF"),
                CommandHandler   = new RibbonCommandHandler("EXF "),
                CommandParameter = "EXF ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildTrenchManagerIcon(32),
                Image            = BuildTrenchManagerIcon(16)
            };
            qaSource.Items.Add(btnTrenchManager);

            qaSource.Items.Add(new RibbonSeparator());

            // ── Help button ────────────────────────────────────────────────
            var btnHelp = new RibbonButton
            {
                Id               = "ALDT_BTN_ALDTHELP",
                Name             = "ALDT Help",
                Text             = "Help",
                Description      = "Open the Advanced Land Development Tools help window " +
                                   "with full command reference and usage guides.",
                ToolTip          = BuildToolTip(
                    "ALDT Help",
                    "Opens the built-in help window with descriptions and step-by-step " +
                    "usage guides for every ALDT command.\n\nCommand: ALDTHELP"),
                CommandHandler   = new RibbonCommandHandler("ALDTHELP "),
                CommandParameter = "ALDTHELP ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildAldtHelpIcon(32),
                Image            = BuildAldtHelpIcon(16)
            };
            qaSource.Items.Add(btnHelp);

            tab.Panels.Add(new RibbonPanel { Source = qaSource });

            // ═══════════════════════════════════════════════════════════════
            //  Panel — Sections
            // ═══════════════════════════════════════════════════════════════
            var secSource = new RibbonPanelSource
            {
                Id    = "ALDT_PANEL_SECTIONS",
                Title = "Sections"
            };

            var btnSecDraw = new RibbonButton
            {
                Id               = "ALDT_BTN_SECDRAW",
                Name             = "Section Drawer",
                Text             = "Section\nDrawer",
                Description      = "Design cross-sections with chained slope segments " +
                                   "and draw them into model space at 1:1 scale.",
                ToolTip          = BuildToolTip(
                    "Section Drawer",
                    "Opens a designer window where you build a cross-section " +
                    "by adding left/right segments with distance and slope %. " +
                    "Live preview updates in real time. Save/load sections for reuse. " +
                    "Click Draw to place into model space.\n\nCommand:  SECDRAW"),
                CommandHandler   = new RibbonCommandHandler("SECDRAW "),
                CommandParameter = "SECDRAW ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildSectionDrawerIcon(32),
                Image            = BuildSectionDrawerIcon(16)
            };
            secSource.Items.Add(btnSecDraw);

            tab.Panels.Add(new RibbonPanel { Source = secSource });

            // ═══════════════════════════════════════════════════════════════
            //  Panel — As-Builts
            // ═══════════════════════════════════════════════════════════════
            var abSource = new RibbonPanelSource
            {
                Id    = "ALDT_PANEL_ASBUILTS",
                Title = "As-Builts"
            };

            var btnCoralAsBuilt = new RibbonButton
            {
                Id               = "ALDT_BTN_CORALASBUILT",
                Name             = "Coral As-Built",
                Text             = "Coral\nAs-Built",
                Description      = "Queries the Coral Gables Sewer GIS within 1000 ft of a " +
                                   "picked point and draws gravity mains, force mains, " +
                                   "manholes, and laterals into model space.",
                ToolTip          = BuildToolTip(
                    "Coral Gables Sewer As-Built",
                    "Pick a center point in the drawing. The tool queries the Coral Gables " +
                    "Sewer ArcGIS FeatureServer and draws all sewer features within the " +
                    "specified radius directly into model space.\n\n" +
                    "Layers:\n" +
                    "  ALDT-CG-GRAVITY-MAIN  (cyan)\n" +
                    "  ALDT-CG-FORCE-MAIN    (red)\n" +
                    "  ALDT-CG-MANHOLE       (yellow)\n" +
                    "  ALDT-CG-LATERAL       (magenta)\n\n" +
                    "Command:  CORALASBUILT"),
                CommandHandler   = new RibbonCommandHandler("CORALASBUILT "),
                CommandParameter = "CORALASBUILT ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildCoralAsBuiltIcon(32),
                Image            = BuildCoralAsBuiltIcon(16)
            };
            abSource.Items.Add(btnCoralAsBuilt);

            abSource.Items.Add(new RibbonSeparator());

            // ── Table Drawer button ────────────────────────────────────────
            var btnTableDraw = new RibbonButton
            {
                Id               = "ALDT_BTN_TABLEDRAW",
                Name             = "Table Drawer",
                Text             = "Table\nDrawer",
                Description      = "Design Excel-like tables with merged cells, free text, and " +
                                   "live links to Civil 3D entities. Draws a grid into model " +
                                   "space and auto-updates when linked entities change.",
                ToolTip          = BuildToolTip(
                    "Table Drawer",
                    "Opens a table designer. Define rows, columns, and cell content.\n\n" +
                    "Each cell can contain:\n" +
                    "  • Free text\n" +
                    "  • A live link to a Civil 3D entity property\n\n" +
                    "Linked cells update automatically when the entity changes.\n" +
                    "Cells can be merged horizontally or vertically.\n" +
                    "Save and reload table layouts for reuse.\n\n" +
                    "Command:  TABLEDRAW"),
                CommandHandler   = new RibbonCommandHandler("TABLEDRAW "),
                CommandParameter = "TABLEDRAW ",
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = System.Windows.Controls.Orientation.Vertical,
                LargeImage       = BuildTableDrawIcon(32),
                Image            = BuildTableDrawIcon(16)
            };
            abSource.Items.Add(btnTableDraw);

            tab.Panels.Add(new RibbonPanel { Source = abSource });

            ribbon.Tabs.Add(tab);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers – programmatic icons until real PNG resources are added
        // ─────────────────────────────────────────────────────────────────────

        private static RibbonToolTip BuildToolTip(string title, string content)
            => new RibbonToolTip
            {
                Title   = title,
                Content = content,
                IsHelpEnabled = false
            };

        /// <summary>Creates a simple 32×32 coloured icon with two-letter text.</summary>
        private static ImageSource BuildButtonIcon(string hexColour, string label)
        {
            int size = 32;
            var visual = new System.Windows.Controls.Grid
            {
                Width  = size,
                Height = size,
                Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(hexColour))
            };
            var txt = new System.Windows.Controls.TextBlock
            {
                Text                = label,
                Foreground          = Brushes.White,
                FontWeight          = System.Windows.FontWeights.Bold,
                FontSize            = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center
            };
            visual.Children.Add(txt);
            return RenderToBitmap(visual, size, size);
        }

        /// <summary>Creates a simple 16×16 coloured icon.</summary>
        private static ImageSource BuildButtonIcon16(string hexColour, string label)
        {
            int size = 16;
            var visual = new System.Windows.Controls.Grid
            {
                Width  = size,
                Height = size,
                Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(hexColour))
            };
            var txt = new System.Windows.Controls.TextBlock
            {
                Text                = label[0].ToString(),
                Foreground          = Brushes.White,
                FontWeight          = System.Windows.FontWeights.Bold,
                FontSize            = 8,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center
            };
            visual.Children.Add(txt);
            return RenderToBitmap(visual, size, size);
        }

        /// <summary>
        /// Draws the Pipe Magic wizard-pipe icon at the given size (32 or 16).
        /// Replicates the SVG design: L-shaped elbow pipe, wizard hat, wand, sparkles.
        /// </summary>
        private static ImageSource BuildPipeMagicIcon(int size)
        {
            // Scale factor so the same geometry works at 32 and 16
            double s = size / 64.0;

            var canvas = new Canvas
            {
                Width      = size,
                Height     = size,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };

            // Helper lambdas
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // ── Background circle ─────────────────────────────────────────────
            Add(new Ellipse
            {
                Width  = size, Height = size,
                Fill   = C("#1a0a2e"),
                Stroke = C("#6a0dad"), StrokeThickness = s * 1.5
            });

            // ── Horizontal pipe section ───────────────────────────────────────
            var hPipe = new Rectangle
            {
                Width  = s * 22, Height = s * 10,
                Fill   = C("#7a7a7a"),
                RadiusX = s * 2, RadiusY = s * 2
            };
            Canvas.SetLeft(hPipe, s * 8);  Canvas.SetTop(hPipe, s * 44);
            Add(hPipe);

            // highlight
            var hHi = new Rectangle
            {
                Width  = s * 22, Height = s * 3,
                Fill   = new SolidColorBrush(Color.FromArgb(128, 0xAA, 0xAA, 0xAA)),
                RadiusX = s * 2, RadiusY = s * 2
            };
            Canvas.SetLeft(hHi, s * 8);  Canvas.SetTop(hHi, s * 44);
            Add(hHi);

            // left end cap
            var lCap = new Ellipse { Width = s * 5, Height = s * 10, Fill = C("#555555") };
            Canvas.SetLeft(lCap, s * 5.5); Canvas.SetTop(lCap, s * 44);
            Add(lCap);

            // ── Elbow (quarter-circle arc rendered as thick stroked path) ─────
            var elbowOuter = new Path
            {
                Data = Geometry.Parse(
                    $"M {s*30},{s*44} Q {s*42},{s*44} {s*42},{s*32}"),
                Stroke          = C("#7a7a7a"),
                StrokeThickness = s * 10,
                Fill            = Brushes.Transparent,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap   = PenLineCap.Flat
            };
            Add(elbowOuter);

            var elbowHi = new Path
            {
                Data = Geometry.Parse(
                    $"M {s*30},{s*44} Q {s*42},{s*44} {s*42},{s*32}"),
                Stroke          = new SolidColorBrush(Color.FromArgb(100, 0xAA, 0xAA, 0xAA)),
                StrokeThickness = s * 3,
                Fill            = Brushes.Transparent
            };
            Add(elbowHi);

            // ── Vertical pipe section ─────────────────────────────────────────
            var vPipe = new Rectangle
            {
                Width  = s * 10, Height = s * 20,
                Fill   = C("#7a7a7a"),
                RadiusX = s * 2, RadiusY = s * 2
            };
            Canvas.SetLeft(vPipe, s * 37); Canvas.SetTop(vPipe, s * 14);
            Add(vPipe);

            var vHi = new Rectangle
            {
                Width  = s * 3, Height = s * 20,
                Fill   = new SolidColorBrush(Color.FromArgb(128, 0xAA, 0xAA, 0xAA)),
                RadiusX = s * 2, RadiusY = s * 2
            };
            Canvas.SetLeft(vHi, s * 37); Canvas.SetTop(vHi, s * 14);
            Add(vHi);

            // top end cap
            var tCap = new Ellipse { Width = s * 10, Height = s * 5, Fill = C("#555555") };
            Canvas.SetLeft(tCap, s * 37); Canvas.SetTop(tCap, s * 11.5);
            Add(tCap);

            // ── Wizard hat brim ───────────────────────────────────────────────
            var brim = new Ellipse { Width = s * 22, Height = s * 6, Fill = C("#4b0082") };
            Canvas.SetLeft(brim, s * 17); Canvas.SetTop(brim, s * 37);
            Add(brim);

            // ── Wizard hat cone ───────────────────────────────────────────────
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*28, s*16),
                    new System.Windows.Point(s*18, s*40),
                    new System.Windows.Point(s*38, s*40)
                }),
                Fill = C("#6a0dad")
            });

            // hat sheen
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*28, s*22),
                    new System.Windows.Point(s*22, s*36),
                    new System.Windows.Point(s*30, s*36)
                }),
                Fill = new SolidColorBrush(Color.FromArgb(90, 0x8B, 0x00, 0xFF))
            });

            // hat band
            var band = new Rectangle
            {
                Width = s * 20, Height = s * 3,
                Fill  = C("#ffd700"),
                RadiusX = s, RadiusY = s
            };
            Canvas.SetLeft(band, s * 18); Canvas.SetTop(band, s * 37);
            Add(band);

            // ── Star on hat ───────────────────────────────────────────────────
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*28, s*19),
                    new System.Windows.Point(s*29, s*22.2),
                    new System.Windows.Point(s*32.4, s*22.2),
                    new System.Windows.Point(s*29.7, s*24.1),
                    new System.Windows.Point(s*30.7, s*27.3),
                    new System.Windows.Point(s*28,   s*25.4),
                    new System.Windows.Point(s*25.3, s*27.3),
                    new System.Windows.Point(s*26.3, s*24.1),
                    new System.Windows.Point(s*23.6, s*22.2),
                    new System.Windows.Point(s*27,   s*22.2)
                }),
                Fill = C("#ffd700")
            });

            // ── Magic wand ────────────────────────────────────────────────────
            Add(new Line
            {
                X1 = s*42, Y1 = s*14, X2 = s*54, Y2 = s*4,
                Stroke = C("#c8a96e"), StrokeThickness = s * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round
            });

            // wand tip glow
            var tipGlow = new Ellipse { Width = s*5, Height = s*5, Fill = C("#ffd700") };
            Canvas.SetLeft(tipGlow, s*51.5); Canvas.SetTop(tipGlow, s*1.5);
            Add(tipGlow);

            // wand sparkle lines
            foreach (var (x1,y1,x2,y2) in new[]{
                (s*54,s*0,  s*54,s*8),
                (s*50,s*4,  s*58,s*4),
                (s*51.5,s*1.5, s*56.5,s*6.5),
                (s*56.5,s*1.5, s*51.5,s*6.5)})
            {
                Add(new Line
                {
                    X1=x1,Y1=y1,X2=x2,Y2=y2,
                    Stroke=C("#ffd700"), StrokeThickness=s*1.2,
                    StrokeStartLineCap=PenLineCap.Round,
                    StrokeEndLineCap=PenLineCap.Round
                });
            }

            // ── Cape drape ────────────────────────────────────────────────────
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*17},{s*40} Q {s*28},{s*50} {s*39},{s*40} " +
                    $"L {s*38},{s*43} Q {s*28},{s*53} {s*18},{s*43} Z"),
                Fill = new SolidColorBrush(Color.FromArgb(204, 0x6a, 0x0d, 0xad))
            });

            // ── Floating sparkles ─────────────────────────────────────────────
            foreach (var (cx,cy,r,hex,alpha) in new[]{
                (s*10, s*32, s*1.5, "#ffd700", (byte)230),
                (s*8,  s*22, s*1.0, "#bf00ff", (byte)200),
                (s*50, s*28, s*1.2, "#ffd700", (byte)180),
                (s*14, s*56, s*1.0, "#bf00ff", (byte)150),
                (s*20, s*18, s*1.5, "#ffd700", (byte)130)})
            {
                var col = (Color)ColorConverter.ConvertFromString(hex);
                col.A   = alpha;
                var sp = new Ellipse
                {
                    Width  = r * 2, Height = r * 2,
                    Fill   = new SolidColorBrush(col)
                };
                Canvas.SetLeft(sp, cx - r); Canvas.SetTop(sp, cy - r);
                Add(sp);
            }

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Bulk Surface Profile icon — stacked terrain profile lines
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildBulkSurfaceIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0a1628"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Grid lines (subtle)
            for (int i = 1; i <= 3; i++)
            {
                Add(new Line { X1 = s*3, Y1 = s*(8*i), X2 = s*29, Y2 = s*(8*i),
                    Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                    StrokeThickness = s * 0.5 });
            }

            // Profile line 1 (back, lighter blue) — terrain shape
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*3},{s*22} L {s*8},{s*18} L {s*12},{s*20} L {s*16},{s*12} " +
                    $"L {s*20},{s*14} L {s*24},{s*10} L {s*29},{s*16}"),
                Stroke = C("#4488CC"), StrokeThickness = s * 1.8,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            // Profile line 2 (front, bright blue) — surface profile
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*3},{s*26} L {s*7},{s*22} L {s*11},{s*24} L {s*15},{s*16} " +
                    $"L {s*19},{s*19} L {s*23},{s*14} L {s*29},{s*20}"),
                Stroke = C("#00AAFF"), StrokeThickness = s * 2.2,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            // Profile line 3 (bottom, green) — design profile
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*3},{s*28} L {s*10},{s*25} L {s*16},{s*23} L {s*22},{s*21} L {s*29},{s*24}"),
                Stroke = C("#44DD44"), StrokeThickness = s * 1.5,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.0 })
            });

            // Axes
            Add(new Line { X1 = s*3, Y1 = s*4, X2 = s*3, Y2 = s*29,
                Stroke = C("#AAAAAA"), StrokeThickness = s * 1 });
            Add(new Line { X1 = s*3, Y1 = s*29, X2 = s*29, Y2 = s*29,
                Stroke = C("#AAAAAA"), StrokeThickness = s * 1 });

            // "x3" multiplier badge
            Add(new Rectangle { Width = s*10, Height = s*6, Fill = C("#0078D4"),
                RadiusX = s*1.5, RadiusY = s*1.5 });
            Canvas.SetLeft(canvas.Children[^1], s * 20); Canvas.SetTop(canvas.Children[^1], s * 2);
            var badge = new System.Windows.Controls.TextBlock
            {
                Text = "x3", Foreground = Brushes.White,
                FontSize = s * 5, FontWeight = System.Windows.FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            Canvas.SetLeft(badge, s * 22); Canvas.SetTop(badge, s * 1.5);
            Add(badge);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Get Parent Alignment icon — alignment with upward link arrow
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildGetParentIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark teal background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0a2020"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Child alignment (bottom, curved line)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*4},{s*26} Q {s*16},{s*20} {s*28},{s*26}"),
                Stroke = C("#66BBBB"), StrokeThickness = s * 2.5,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            // Small dot on child alignment (selection point)
            Add(new Ellipse { Width = s*4, Height = s*4, Fill = C("#FFDD44") });
            Canvas.SetLeft(canvas.Children[^1], s * 14); Canvas.SetTop(canvas.Children[^1], s * 20.5);

            // Upward arrow (link to parent)
            Add(new Line { X1 = s*16, Y1 = s*19, X2 = s*16, Y2 = s*10,
                Stroke = C("#FFDD44"), StrokeThickness = s * 1.5 });
            // Arrowhead
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*7),
                    new System.Windows.Point(s*13, s*11),
                    new System.Windows.Point(s*19, s*11)
                }),
                Fill = C("#FFDD44")
            });

            // Parent alignment (top, straighter bold line)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*2},{s*8} Q {s*10},{s*4} {s*16},{s*5} Q {s*22},{s*6} {s*30},{s*3}"),
                Stroke = C("#00DDBB"), StrokeThickness = s * 3,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            // Small station ticks on parent
            for (double x = 6; x <= 26; x += 5)
            {
                double y = 5 + (x - 16) * (x - 16) * 0.01;  // approximate curve
                Add(new Line { X1 = s*x, Y1 = s*(y-1.5), X2 = s*x, Y2 = s*(y+1.5),
                    Stroke = C("#00DDBB"), StrokeThickness = s * 1 });
            }

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Align Deploy icon — main alignment with perpendicular cross lines
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildAlignDeployIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark green background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0a1a0a"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Main alignment (horizontal curved line)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*2},{s*18} Q {s*10},{s*12} {s*16},{s*14} Q {s*22},{s*16} {s*30},{s*14}"),
                Stroke = C("#44DD44"), StrokeThickness = s * 2.5,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            // Cross alignments (perpendicular short lines at intervals)
            double[] xs = { 8, 14, 20, 26 };
            double[] ys = { 14, 13.5, 14.8, 14.2 };
            for (int i = 0; i < xs.Length; i++)
            {
                Add(new Line
                {
                    X1 = s * xs[i], Y1 = s * (ys[i] - 6),
                    X2 = s * xs[i], Y2 = s * (ys[i] + 6),
                    Stroke = C("#88FF88"), StrokeThickness = s * 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
                // Small dot at intersection
                Add(new Ellipse { Width = s*2.5, Height = s*2.5, Fill = C("#FFFFFF") });
                Canvas.SetLeft(canvas.Children[^1], s * (xs[i] - 1.25));
                Canvas.SetTop(canvas.Children[^1], s * (ys[i] - 1.25));
            }

            // Interval arrows between cross alignments
            for (int i = 0; i < xs.Length - 1; i++)
            {
                double midX = (xs[i] + xs[i + 1]) / 2.0;
                Add(new Line
                {
                    X1 = s * (xs[i] + 1.5), Y1 = s * 24,
                    X2 = s * (xs[i + 1] - 1.5), Y2 = s * 24,
                    Stroke = C("#AADDAA"), StrokeThickness = s * 0.8,
                    StrokeDashArray = new DoubleCollection(new[] { 2.0, 1.5 })
                });
            }

            // "d=" spacing label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "d=", Foreground = C("#AADDAA"),
                FontSize = s * 4.5, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(label, s * 2); Canvas.SetTop(label, s * 25);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Invert Pull Up icon — pipe with elevation dimension arrow
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildInvertPullUpIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark warm background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1a1408"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Ground line
            Add(new Line { X1 = s*2, Y1 = s*10, X2 = s*30, Y2 = s*10,
                Stroke = C("#886644"), StrokeThickness = s * 1.5,
                StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.0 }) });

            // Pipe body (sloped rectangle from upper-left to lower-right)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*4},{s*16} L {s*28},{s*22} L {s*28},{s*26} L {s*4},{s*20} Z"),
                Fill = C("#8B7355"),
                Stroke = C("#AA9060"), StrokeThickness = s * 1
            });

            // Pipe highlight
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*4},{s*16} L {s*28},{s*22} L {s*28},{s*23} L {s*4},{s*17} Z"),
                Fill = new SolidColorBrush(Color.FromArgb(80, 255, 220, 150))
            });

            // Left manhole
            Add(new Rectangle { Width = s*4, Height = s*8, Fill = C("#666666") });
            Canvas.SetLeft(canvas.Children[^1], s * 2); Canvas.SetTop(canvas.Children[^1], s * 10);
            // Right manhole
            Add(new Rectangle { Width = s*4, Height = s*14, Fill = C("#666666") });
            Canvas.SetLeft(canvas.Children[^1], s * 26); Canvas.SetTop(canvas.Children[^1], s * 10);

            // Pick point (yellow dot on pipe)
            Add(new Ellipse { Width = s*4, Height = s*4, Fill = C("#FFD700") });
            Canvas.SetLeft(canvas.Children[^1], s * 14); Canvas.SetTop(canvas.Children[^1], s * 17.5);

            // Vertical dimension arrow from pick point down to invert
            Add(new Line { X1 = s*16, Y1 = s*22, X2 = s*16, Y2 = s*29,
                Stroke = C("#FF6644"), StrokeThickness = s * 1.2 });
            // Down arrowhead
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*30),
                    new System.Windows.Point(s*14, s*27.5),
                    new System.Windows.Point(s*18, s*27.5)
                }),
                Fill = C("#FF6644")
            });

            // "EL" elevation label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "EL", Foreground = C("#FF6644"),
                FontSize = s * 4.5, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(label, s * 19); Canvas.SetTop(label, s * 25);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Change Elevation icon — pipe with bidirectional level arrows
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildChangeElevationIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark blue-gray background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0d1520"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Pipe body (sloped from upper-left to lower-right)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*3},{s*12} L {s*29},{s*20} L {s*29},{s*24} L {s*3},{s*16} Z"),
                Fill = C("#8B7355"),
                Stroke = C("#AA9060"), StrokeThickness = s * 1
            });

            // Pipe highlight
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*3},{s*12} L {s*29},{s*20} L {s*29},{s*21} L {s*3},{s*13} Z"),
                Fill = new SolidColorBrush(Color.FromArgb(80, 255, 220, 150))
            });

            // Left elevation marker (horizontal dashed line)
            Add(new Line { X1 = s*1, Y1 = s*14, X2 = s*10, Y2 = s*14,
                Stroke = C("#44BBFF"), StrokeThickness = s * 0.8,
                StrokeDashArray = new DoubleCollection(new[] { 2.0, 1.5 }) });

            // Right elevation marker (horizontal dashed line)
            Add(new Line { X1 = s*22, Y1 = s*22, X2 = s*31, Y2 = s*22,
                Stroke = C("#FF6644"), StrokeThickness = s * 0.8,
                StrokeDashArray = new DoubleCollection(new[] { 2.0, 1.5 }) });

            // Vertical arrow showing level change (center of pipe)
            Add(new Line { X1 = s*16, Y1 = s*14, X2 = s*16, Y2 = s*22,
                Stroke = C("#44DD44"), StrokeThickness = s * 1.5 });
            // Up arrowhead
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*12),
                    new System.Windows.Point(s*14, s*15),
                    new System.Windows.Point(s*18, s*15)
                }),
                Fill = C("#44DD44")
            });
            // Down arrowhead
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*24),
                    new System.Windows.Point(s*14, s*21),
                    new System.Windows.Point(s*18, s*21)
                }),
                Fill = C("#44DD44")
            });

            // "=" equals sign (level pipe concept)
            var eqLabel = new System.Windows.Controls.TextBlock
            {
                Text = "=", Foreground = C("#44DD44"),
                FontSize = s * 6, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(eqLabel, s * 22); Canvas.SetTop(eqLabel, s * 4);
            Add(eqLabel);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Flood Zone Lookup icon — water waves with map pin
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildFloodZoneIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark blue background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#081828"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Water fill (bottom half)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M 0,{s*18} Q {s*5},{s*15} {s*10},{s*18} Q {s*15},{s*21} {s*20},{s*18} " +
                    $"Q {s*25},{s*15} {s*32},{s*18} L {s*32},{s*32} L 0,{s*32} Z"),
                Fill = new SolidColorBrush(Color.FromArgb(140, 0x1E, 0x90, 0xFF))
            });

            // Wave line 1
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*0},{s*18} Q {s*5},{s*15} {s*10},{s*18} Q {s*15},{s*21} " +
                    $"{s*20},{s*18} Q {s*25},{s*15} {s*32},{s*18}"),
                Stroke = C("#4DB8FF"), StrokeThickness = s * 1.8,
                Fill = Brushes.Transparent
            });

            // Wave line 2
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*0},{s*23} Q {s*5},{s*20} {s*10},{s*23} Q {s*15},{s*26} " +
                    $"{s*20},{s*23} Q {s*25},{s*20} {s*32},{s*23}"),
                Stroke = C("#3388CC"), StrokeThickness = s * 1.3,
                Fill = Brushes.Transparent
            });

            // Wave line 3
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*0},{s*27} Q {s*5},{s*25} {s*10},{s*27} Q {s*15},{s*29} " +
                    $"{s*20},{s*27} Q {s*25},{s*25} {s*32},{s*27}"),
                Stroke = C("#225588"), StrokeThickness = s * 1,
                Fill = Brushes.Transparent
            });

            // Map pin (red with white center)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*16},{s*16} C {s*16},{s*10} {s*10},{s*6} {s*10},{s*10} " +
                    $"C {s*10},{s*13} {s*16},{s*16} {s*16},{s*16} Z"),
                Fill = C("#FF3333")
            });
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*16},{s*16} C {s*16},{s*10} {s*22},{s*6} {s*22},{s*10} " +
                    $"C {s*22},{s*13} {s*16},{s*16} {s*16},{s*16} Z"),
                Fill = C("#DD2222")
            });
            // Pin center dot
            Add(new Ellipse { Width = s*4, Height = s*4, Fill = C("#FFFFFF") });
            Canvas.SetLeft(canvas.Children[^1], s * 14); Canvas.SetTop(canvas.Children[^1], s * 7.5);

            // "FEMA" small text
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "FEMA", Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                FontSize = s * 3.5, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(label, s * 2); Canvas.SetTop(label, s * 1.5);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Flood Criteria icon — water level gauge with elevation marker
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildFloodCriteriaIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark orange-tinted background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1a1008"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Ruler/gauge on left side
            Add(new Rectangle { Width = s*5, Height = s*26, Fill = C("#554422"),
                RadiusX = s * 0.5, RadiusY = s * 0.5 });
            Canvas.SetLeft(canvas.Children[^1], s * 3); Canvas.SetTop(canvas.Children[^1], s * 3);

            // Ruler ticks
            for (int i = 0; i < 6; i++)
            {
                double y = 5 + i * 4.5;
                double tickLen = (i % 2 == 0) ? 4 : 2.5;
                Add(new Line { X1 = s*3, Y1 = s*y, X2 = s*(3+tickLen), Y2 = s*y,
                    Stroke = C("#CCAA66"), StrokeThickness = s * 0.8 });
            }

            // Water level (horizontal line across)
            Add(new Line { X1 = s*8, Y1 = s*14, X2 = s*29, Y2 = s*14,
                Stroke = C("#FF8C00"), StrokeThickness = s * 2,
                StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.0 }) });

            // Water fill below the line
            Add(new Rectangle { Width = s*21, Height = s*15,
                Fill = new SolidColorBrush(Color.FromArgb(60, 0xFF, 0x8C, 0x00)) });
            Canvas.SetLeft(canvas.Children[^1], s * 8); Canvas.SetTop(canvas.Children[^1], s * 14);

            // Elevation arrow (pointing at the water line)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*10, s*14),
                    new System.Windows.Point(s*12, s*11),
                    new System.Windows.Point(s*14, s*14)
                }),
                Fill = C("#FFB833")
            });

            // Elevation value text
            var elevText = new System.Windows.Controls.TextBlock
            {
                Text = "EL", Foreground = C("#FFB833"),
                FontSize = s * 5, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(elevText, s * 17); Canvas.SetTop(elevText, s * 4);
            Add(elevText);

            // "ft" unit
            var unitText = new System.Windows.Controls.TextBlock
            {
                Text = "ft", Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 200, 100)),
                FontSize = s * 4
            };
            Canvas.SetLeft(unitText, s * 24); Canvas.SetTop(unitText, s * 5);
            Add(unitText);

            // Small building silhouette (to suggest criteria context)
            Add(new Rectangle { Width = s*6, Height = s*8, Fill = C("#665533") });
            Canvas.SetLeft(canvas.Children[^1], s * 20); Canvas.SetTop(canvas.Children[^1], s * 21);
            // Building roof
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*19, s*21),
                    new System.Windows.Point(s*23, s*17.5),
                    new System.Windows.Point(s*27, s*21)
                }),
                Fill = C("#887755")
            });

            // "MDC" label
            var mdcLabel = new System.Windows.Controls.TextBlock
            {
                Text = "MDC", Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                FontSize = s * 3, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(mdcLabel, s * 11); Canvas.SetTop(mdcLabel, s * 26);
            Add(mdcLabel);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Section Lookup icon — grid/survey with crosshair
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildSectionLookupIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark green-brown background (survey/land)
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0f1a0a"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Grid lines (township/range grid)
            for (int i = 1; i <= 3; i++)
            {
                double pos = s * (6 + i * 6);
                // Vertical
                Add(new Line { X1 = pos, Y1 = s*4, X2 = pos, Y2 = s*28,
                    Stroke = C("#558844"), StrokeThickness = s * 0.8 });
                // Horizontal
                Add(new Line { X1 = s*4, Y1 = pos, X2 = s*28, Y2 = pos,
                    Stroke = C("#558844"), StrokeThickness = s * 0.8 });
            }

            // Outer border of grid
            Add(new Rectangle { Width = s*24, Height = s*24,
                Stroke = C("#77AA55"), StrokeThickness = s * 1.2,
                Fill = Brushes.Transparent });
            Canvas.SetLeft(canvas.Children[^1], s * 4); Canvas.SetTop(canvas.Children[^1], s * 4);

            // Highlighted section (one cell filled)
            Add(new Rectangle { Width = s*6, Height = s*6,
                Fill = new SolidColorBrush(Color.FromArgb(100, 0xFF, 0xAA, 0x00)) });
            Canvas.SetLeft(canvas.Children[^1], s * 12); Canvas.SetTop(canvas.Children[^1], s * 12);

            // Crosshair at center of highlighted section
            double cx = s * 15, cy = s * 15;
            Add(new Line { X1 = cx - s*3, Y1 = cy, X2 = cx + s*3, Y2 = cy,
                Stroke = C("#FFDD00"), StrokeThickness = s * 1.2 });
            Add(new Line { X1 = cx, Y1 = cy - s*3, X2 = cx, Y2 = cy + s*3,
                Stroke = C("#FFDD00"), StrokeThickness = s * 1.2 });
            Add(new Ellipse { Width = s*4, Height = s*4,
                Stroke = C("#FFDD00"), StrokeThickness = s * 0.8,
                Fill = Brushes.Transparent });
            Canvas.SetLeft(canvas.Children[^1], cx - s*2); Canvas.SetTop(canvas.Children[^1], cy - s*2);

            // "TRS" label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "TRS", Foreground = C("#FFDD00"),
                FontSize = s * 4, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(label, s * 2); Canvas.SetTop(label, s * 0.5);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Mark Lines icon — horizontal alignment line with vertical markers
        // ═══════════════════════════════════════════════════════════════════
        private static ImageSource BuildMarkLinesIcon(int size)
        {
            double s = size / 32.0;

            var canvas = new Canvas
            {
                Width = size, Height = size,
                Background = Brushes.Transparent, ClipToBounds = true
            };

            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background rounded rect
            Add(new Rectangle
            {
                Width = size, Height = size,
                Fill = C("#1B3A4B"),
                RadiusX = s * 4, RadiusY = s * 4
            });

            // Horizontal alignment line (the alignment)
            Add(new Line
            {
                X1 = s * 2, Y1 = s * 16, X2 = s * 30, Y2 = s * 16,
                Stroke = C("#4CAF50"), StrokeThickness = s * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            // Vertical marker lines (crossing lines → vertical marks)
            foreach (double x in new[] { s * 8, s * 16, s * 24 })
            {
                Add(new Line
                {
                    X1 = x, Y1 = s * 4, X2 = x, Y2 = s * 28,
                    Stroke = C("#FF5252"), StrokeThickness = s * 1.8,
                    StrokeDashArray = new DoubleCollection(new[] { 3.0, 1.5 }),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            // Small crossing indicators at intersections
            foreach (double x in new[] { s * 8, s * 16, s * 24 })
            {
                Add(new Ellipse
                {
                    Width = s * 4, Height = s * 4,
                    Fill = C("#FFAB40")
                });
                Canvas.SetLeft(canvas.Children[^1], x - s * 2);
                Canvas.SetTop(canvas.Children[^1], s * 14);
            }

            // "ML" label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "ML", Foreground = C("#FFFFFF"),
                FontSize = s * 3.5, FontWeight = System.Windows.FontWeights.Bold,
                Opacity = 0.7
            };
            Canvas.SetLeft(label, s * 1); Canvas.SetTop(label, s * 0.5);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Mark Fittings icon
        // ═══════════════════════════════════════════════════════════════════
        private static ImageSource BuildMarkFittingsIcon(int size)
        {
            double s = size / 32.0;

            var canvas = new Canvas
            {
                Width = size, Height = size,
                Background = Brushes.Transparent, ClipToBounds = true
            };

            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background rounded rect
            Add(new Rectangle
            {
                Width = size, Height = size,
                Fill = C("#2E3B32"),
                RadiusX = s * 4, RadiusY = s * 4
            });

            // Horizontal alignment line (the alignment)
            Add(new Line
            {
                X1 = s * 2, Y1 = s * 16, X2 = s * 30, Y2 = s * 16,
                Stroke = C("#8BC34A"), StrokeThickness = s * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            // Vertical marker lines
            foreach (double x in new[] { s * 10, s * 22 })
            {
                Add(new Line
                {
                    X1 = x, Y1 = s * 4, X2 = x, Y2 = s * 28,
                    Stroke = C("#FF9800"), StrokeThickness = s * 2,
                    StrokeDashArray = new DoubleCollection(new[] { 3.0, 1.5 }),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            // Small crossing indicators (representing fittings)
            foreach (double x in new[] { s * 10, s * 22 })
            {
                Add(new Rectangle
                {
                    Width = s * 6, Height = s * 4,
                    Fill = C("#03A9F4")
                });
                Canvas.SetLeft(canvas.Children[^1], x - s * 3);
                Canvas.SetTop(canvas.Children[^1], s * 14);
            }

            // "MF" label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "MF", Foreground = C("#FFFFFF"),
                FontSize = s * 3.5, FontWeight = System.Windows.FontWeights.Bold,
                Opacity = 0.7
            };
            Canvas.SetLeft(label, s * 1); Canvas.SetTop(label, s * 0.5);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        private static ImageSource BuildGroundwaterIcon(int size)
        {
            double s = size / 32.0;

            var canvas = new Canvas
            {
                Width = size, Height = size,
                Background = Brushes.Transparent, ClipToBounds = true
            };

            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background rounded rect — dark blue-green
            Add(new Rectangle
            {
                Width = size, Height = size,
                Fill = C("#0D3B4F"),
                RadiusX = s * 4, RadiusY = s * 4
            });

            // Water table wavy line
            var wave = new Polyline
            {
                Stroke = C("#29B6F6"), StrokeThickness = s * 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            wave.Points.Add(new System.Windows.Point(s * 2, s * 18));
            wave.Points.Add(new System.Windows.Point(s * 8, s * 15));
            wave.Points.Add(new System.Windows.Point(s * 16, s * 19));
            wave.Points.Add(new System.Windows.Point(s * 24, s * 14));
            wave.Points.Add(new System.Windows.Point(s * 30, s * 17));
            Add(wave);

            // Ground surface line
            Add(new Line
            {
                X1 = s * 2, Y1 = s * 8, X2 = s * 30, Y2 = s * 8,
                Stroke = C("#8D6E63"), StrokeThickness = s * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            // Down arrow (indicating depth to groundwater)
            Add(new Line
            {
                X1 = s * 16, Y1 = s * 10, X2 = s * 16, Y2 = s * 26,
                Stroke = C("#66BB6A"), StrokeThickness = s * 1.8,
                StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.0 })
            });
            // Arrow head
            Add(new Line
            {
                X1 = s * 13, Y1 = s * 23, X2 = s * 16, Y2 = s * 26,
                Stroke = C("#66BB6A"), StrokeThickness = s * 1.8,
                StrokeStartLineCap = PenLineCap.Round
            });
            Add(new Line
            {
                X1 = s * 19, Y1 = s * 23, X2 = s * 16, Y2 = s * 26,
                Stroke = C("#66BB6A"), StrokeThickness = s * 1.8,
                StrokeStartLineCap = PenLineCap.Round
            });

            // "GW" label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "GW", Foreground = C("#FFFFFF"),
                FontSize = s * 3.5, FontWeight = System.Windows.FontWeights.Bold,
                Opacity = 0.8
            };
            Canvas.SetLeft(label, s * 1); Canvas.SetTop(label, s * 0.5);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        private static ImageSource RenderToBitmap(
            System.Windows.FrameworkElement visual, int width, int height)
        {
            visual.Measure(new System.Windows.Size(width, height));
            visual.Arrange(new System.Windows.Rect(0, 0, width, height));
            visual.UpdateLayout();

            var rtb = new RenderTargetBitmap(
                width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            return rtb;
        }

        // ── Mini Toolbar icon ───────────────────────────────────────────
        private static ImageSource BuildMiniToolbarIcon(int size)
        {
            var canvas = new Canvas { Width = size, Height = size };

            // Dark background with rounded feel
            canvas.Children.Add(new Rectangle
            {
                Width  = size,
                Height = size,
                RadiusX = size * 0.2,
                RadiusY = size * 0.2,
                Fill = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#1A1A2E"))
            });

            // Vertical toolbar shape (rounded rect outline)
            double tw = size * 0.4;
            double th = size * 0.75;
            double tx = (size - tw) / 2;
            double ty = (size - th) / 2;
            var toolbar = new Rectangle
            {
                Width  = tw,
                Height = th,
                RadiusX = 3,
                RadiusY = 3,
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#60CDFF")),
                StrokeThickness = size > 20 ? 1.5 : 1,
                Fill = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#2D2D2D"))
            };
            Canvas.SetLeft(toolbar, tx);
            Canvas.SetTop(toolbar, ty);
            canvas.Children.Add(toolbar);

            // Three small icon dots inside
            double dotSize = size > 20 ? 4 : 2;
            double dotX = (size - dotSize) / 2;
            for (int i = 0; i < 3; i++)
            {
                double dotY = ty + th * 0.22 + i * (th * 0.22);
                var dot = new Rectangle
                {
                    Width  = dotSize,
                    Height = dotSize,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString("#60CDFF"))
                };
                Canvas.SetLeft(dot, dotX);
                Canvas.SetTop(dot, dotY);
                canvas.Children.Add(dot);
            }

            return RenderToBitmap(canvas, size, size);
        }

        // ── EXF Trench Manager icon — trench cross-section + ruler ───────
        private static ImageSource BuildTrenchManagerIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1A1A2E"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Earth / ground fill ───────────────────────────────────────────
            Add(new Rectangle { Width = size, Height = s * 14,
                Fill = C("#3E2723") });
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], s * 18);

            // ── Trench cut-out (U shape in earth) ────────────────────────────
            // Left wall
            Add(new Rectangle { Width = s * 6, Height = s * 14,
                Fill = C("#1A1A2E") });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * 8);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], s * 18);

            // Right wall
            Add(new Rectangle { Width = s * 6, Height = s * 14,
                Fill = C("#1A1A2E") });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * 18);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], s * 18);

            // Trench bottom (slightly lighter)
            Add(new Rectangle { Width = s * 10, Height = s * 3,
                Fill = C("#263238") });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * 8);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], s * 29);

            // ── Trench outline ───────────────────────────────────────────────
            var outline = C("#FF8A65");
            double lw = s * 1.4;
            // Left wall line
            Add(new Line { X1=s*8,  Y1=s*18, X2=s*8,  Y2=s*32, Stroke=outline, StrokeThickness=lw });
            // Right wall line
            Add(new Line { X1=s*24, Y1=s*18, X2=s*24, Y2=s*32, Stroke=outline, StrokeThickness=lw });
            // Bottom line
            Add(new Line { X1=s*8,  Y1=s*29, X2=s*24, Y2=s*29, Stroke=outline, StrokeThickness=lw });

            // ── Ground surface line ──────────────────────────────────────────
            Add(new Line { X1=s*0, Y1=s*18, X2=s*32, Y2=s*18,
                Stroke=C("#8D6E63"), StrokeThickness=s*1.5 });

            // ── Dimension arrow showing longest segment (top arrow) ───────────
            var dimColor = C("#4DD0E1");
            double aw = s * 1.3;
            // Arrow shaft
            Add(new Line { X1=s*8, Y1=s*12, X2=s*24, Y2=s*12,
                Stroke=dimColor, StrokeThickness=aw });
            // Left tick
            Add(new Line { X1=s*8, Y1=s*10, X2=s*8, Y2=s*14,
                Stroke=dimColor, StrokeThickness=aw });
            // Right tick
            Add(new Line { X1=s*24, Y1=s*10, X2=s*24, Y2=s*14,
                Stroke=dimColor, StrokeThickness=aw });

            // ── Small ruler ticks on top ─────────────────────────────────────
            Add(new Line { X1=s*12, Y1=s*12, X2=s*12, Y2=s*10,
                Stroke=dimColor, StrokeThickness=s*0.8 });
            Add(new Line { X1=s*16, Y1=s*12, X2=s*16, Y2=s*10,
                Stroke=dimColor, StrokeThickness=s*0.8 });
            Add(new Line { X1=s*20, Y1=s*12, X2=s*20, Y2=s*10,
                Stroke=dimColor, StrokeThickness=s*0.8 });

            // ── Length label ─────────────────────────────────────────────────
            var lbl = new System.Windows.Controls.TextBlock
            {
                Text = "L",
                FontSize = s * 7,
                FontWeight = System.Windows.FontWeights.Bold,
                Foreground = C("#FFF176")
            };
            Canvas.SetLeft(lbl, s * 14); Canvas.SetTop(lbl, s * 2); Add(lbl);

            return RenderToBitmap(canvas, size, size);
        }

        // ── Area Manager icon ─────────────────────────────────────────
        private static ImageSource BuildAreaManagerIcon(int size)
        {
            var canvas = new Canvas { Width = size, Height = size };

            // Dark background
            canvas.Children.Add(new Rectangle
            {
                Width  = size,
                Height = size,
                RadiusX = size * 0.2,
                RadiusY = size * 0.2,
                Fill = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#1A2E1A"))
            });

            // Grid/area icon — overlapping squares
            double s1 = size * 0.45;
            double s2 = size * 0.4;
            double x1 = size * 0.15, y1 = size * 0.15;
            double x2 = size * 0.4,  y2 = size * 0.4;

            var rect1 = new Rectangle
            {
                Width  = s1, Height = s1,
                RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#4CAF50")) { Opacity = 0.5 },
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#81C784")),
                StrokeThickness = size > 20 ? 1.5 : 1
            };
            Canvas.SetLeft(rect1, x1);
            Canvas.SetTop(rect1, y1);
            canvas.Children.Add(rect1);

            var rect2 = new Rectangle
            {
                Width  = s2, Height = s2,
                RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#2196F3")) { Opacity = 0.5 },
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#64B5F6")),
                StrokeThickness = size > 20 ? 1.5 : 1
            };
            Canvas.SetLeft(rect2, x2);
            Canvas.SetTop(rect2, y2);
            canvas.Children.Add(rect2);

            return RenderToBitmap(canvas, size, size);
        }

        // ── VP Cut icon — scissors cutting a viewport rectangle ──────────
        private static ImageSource BuildVpCutIcon(int size)
        {
            var canvas = new Canvas { Width = size, Height = size };

            // Dark background
            canvas.Children.Add(new Rectangle
            {
                Width   = size,
                Height  = size,
                RadiusX = size * 0.2,
                RadiusY = size * 0.2,
                Fill    = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#1A1A2E"))
            });

            double t = size > 20 ? 1.5 : 1;

            // Original viewport rectangle (top-left, faded)
            var vpRect = new Rectangle
            {
                Width  = size * 0.55,
                Height = size * 0.55,
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#5C6BC0")) { Opacity = 0.4 },
                StrokeThickness = t,
                StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.0 })
            };
            Canvas.SetLeft(vpRect, size * 0.08);
            Canvas.SetTop(vpRect, size * 0.12);
            canvas.Children.Add(vpRect);

            // Cut piece 1 (top-right)
            var cut1 = new Rectangle
            {
                Width  = size * 0.35,
                Height = size * 0.3,
                RadiusX = 2, RadiusY = 2,
                Fill   = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#42A5F5")) { Opacity = 0.6 },
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#90CAF9")),
                StrokeThickness = t
            };
            Canvas.SetLeft(cut1, size * 0.55);
            Canvas.SetTop(cut1, size * 0.08);
            canvas.Children.Add(cut1);

            // Cut piece 2 (bottom-right)
            var cut2 = new Rectangle
            {
                Width  = size * 0.4,
                Height = size * 0.35,
                RadiusX = 2, RadiusY = 2,
                Fill   = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#EF5350")) { Opacity = 0.6 },
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#EF9A9A")),
                StrokeThickness = t
            };
            Canvas.SetLeft(cut2, size * 0.50);
            Canvas.SetTop(cut2, size * 0.55);
            canvas.Children.Add(cut2);

            // Diagonal cut line
            var cutLine = new System.Windows.Shapes.Line
            {
                X1 = size * 0.15, Y1 = size * 0.85,
                X2 = size * 0.85, Y2 = size * 0.15,
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#FFB74D")),
                StrokeThickness = t * 1.2,
                StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.0 })
            };
            canvas.Children.Add(cutLine);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Low Rim icon — structure with down arrow to lowest point
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildLowRimIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0d1520"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Manhole structure (trapezoid/rect)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*10},{s*6} L {s*22},{s*6} L {s*24},{s*18} L {s*8},{s*18} Z"),
                Fill = C("#6D4C41"),
                Stroke = C("#8D6E63"), StrokeThickness = s * 1
            });

            // Rim line at top
            Add(new Line { X1 = s*6, Y1 = s*6, X2 = s*26, Y2 = s*6,
                Stroke = C("#FFB74D"), StrokeThickness = s * 1.5 });

            // Down arrow (pointing to lowest)
            Add(new Line { X1 = s*16, Y1 = s*20, X2 = s*16, Y2 = s*29,
                Stroke = C("#EF5350"), StrokeThickness = s * 1.5 });
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*31),
                    new System.Windows.Point(s*13, s*27),
                    new System.Windows.Point(s*19, s*27)
                }),
                Fill = C("#EF5350")
            });

            // "Lo" label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "Lo", Foreground = C("#4FC3F7"),
                FontSize = s * 5, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(label, s * 2); Canvas.SetTop(label, s * 22);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }
        // ═════════════════════════════════════════════════════════════════════
        //  Elev Slope icon — sloped line from point A to point B with %
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildElevSlopeIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0d1520"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Ground line
            Add(new Line { X1 = s*2, Y1 = s*26, X2 = s*30, Y2 = s*26,
                Stroke = C("#5D4037"), StrokeThickness = s * 1.5 });

            // Slope line (rising left to right)
            Add(new Line { X1 = s*4, Y1 = s*22, X2 = s*28, Y2 = s*12,
                Stroke = C("#66BB6A"), StrokeThickness = s * 1.5 });

            // Start point dot
            Add(new Ellipse { Width = s*5, Height = s*5, Fill = C("#4FC3F7") });
            Canvas.SetLeft(canvas.Children[^1], s * 2); Canvas.SetTop(canvas.Children[^1], s * 20);

            // End point dot
            Add(new Ellipse { Width = s*5, Height = s*5, Fill = C("#FFB74D") });
            Canvas.SetLeft(canvas.Children[^1], s * 26); Canvas.SetTop(canvas.Children[^1], s * 10);

            // "%" label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "%", Foreground = C("#EF5350"),
                FontSize = s * 7, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(label, s * 12); Canvas.SetTop(label, s * 2);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        // ─────────────────────────────────────────────────────────────
        //  Vehicle Tracking Icons
        // ─────────────────────────────────────────────────────────────

        private static ImageSource BuildVtPanelIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size };
            void Add(System.Windows.UIElement e) => canvas.Children.Add(e);
            Brush C(string hex) => new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));

            // Dark background
            var bg = new System.Windows.Shapes.Rectangle
            { Width = size, Height = size, Fill = C("#1A237E"), RadiusX = s*4, RadiusY = s*4 };
            Add(bg);

            // Truck body outline
            var body = new System.Windows.Shapes.Rectangle
            {
                Width = s * 22, Height = s * 10,
                Stroke = C("#42A5F5"), StrokeThickness = s * 1.5,
                Fill = C("#1E88E5"), RadiusX = s*2, RadiusY = s*2
            };
            Canvas.SetLeft(body, s * 5); Canvas.SetTop(body, s * 8);
            Add(body);

            // Cab
            var cab = new System.Windows.Shapes.Rectangle
            {
                Width = s * 8, Height = s * 10,
                Stroke = C("#42A5F5"), StrokeThickness = s * 1.5,
                Fill = C("#2196F3"), RadiusX = s*2, RadiusY = s*2
            };
            Canvas.SetLeft(cab, s * 22); Canvas.SetTop(cab, s * 8);
            Add(cab);

            // Wheels
            foreach (double wx in new[] { s*8, s*15, s*24 })
            {
                var wheel = new System.Windows.Shapes.Ellipse
                {
                    Width = s * 5, Height = s * 5,
                    Fill = C("#FFB74D"), Stroke = C("#FF9800"), StrokeThickness = s * 0.5
                };
                Canvas.SetLeft(wheel, wx); Canvas.SetTop(wheel, s * 17);
                Add(wheel);
            }

            // Arrow (swept path indicator)
            var arrow = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(
                    $"M{s*4},{s*5} C{s*10},{s*2} {s*20},{s*2} {s*28},{s*5}"),
                Stroke = C("#66BB6A"), StrokeThickness = s * 1.5,
                Fill = null
            };
            Add(arrow);

            return RenderToBitmap(canvas, size, size);
        }

        private static ImageSource BuildVtSweepIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size };
            void Add(System.Windows.UIElement e) => canvas.Children.Add(e);
            Brush C(string hex) => new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));

            var bg = new System.Windows.Shapes.Rectangle
            { Width = size, Height = size, Fill = C("#1B5E20"), RadiusX = s*4, RadiusY = s*4 };
            Add(bg);

            // Curved path
            var path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(
                    $"M{s*4},{s*28} C{s*8},{s*14} {s*18},{s*8} {s*28},{s*4}"),
                Stroke = C("#A5D6A7"), StrokeThickness = s * 8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.4
            };
            Add(path);

            // Center line
            var center = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(
                    $"M{s*4},{s*28} C{s*8},{s*14} {s*18},{s*8} {s*28},{s*4}"),
                Stroke = C("#66BB6A"), StrokeThickness = s * 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 }
            };
            Add(center);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Pipe Sizing icon — pipe cross-section (left) + ruler (right)
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildPipeSizingIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0D1B2A"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Left half: pipe cross-section ─────────────────────────────────
            // Pipe outer wall (thick ring, steel grey)
            Add(new System.Windows.Shapes.Ellipse
            {
                Width = s * 15, Height = s * 15,
                Stroke = C("#78909C"), StrokeThickness = s * 2.5,
                Fill = C("#102030")
            });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * 1);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], s * 8.5);

            // Water fill — lower portion of pipe interior
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*2.5},{s*16} A {s*7},{s*7} 0 0 0 {s*14.5},{s*16} Z"),
                Fill = C("#1565C0"),
                Opacity = 0.85
            });

            // Pipe inner bore highlight (top arc, lighter)
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*3.5},{s*16} A {s*5},{s*5} 0 0 1 {s*13.5},{s*16}"),
                Stroke = C("#B0BEC5"), StrokeThickness = s * 0.7,
                Fill = C("Transparent")
            });

            // Diameter dimension line (horizontal across pipe center)
            Add(new Line { X1 = s*1, Y1 = s*16, X2 = s*16, Y2 = s*16,
                Stroke = C("#4FC3F7"), StrokeThickness = s * 0.8 });
            // End ticks
            Add(new Line { X1 = s*1,  Y1 = s*14.5, X2 = s*1,  Y2 = s*17.5,
                Stroke = C("#4FC3F7"), StrokeThickness = s * 0.8 });
            Add(new Line { X1 = s*16, Y1 = s*14.5, X2 = s*16, Y2 = s*17.5,
                Stroke = C("#4FC3F7"), StrokeThickness = s * 0.8 });

            // "Ø" diameter label below dimension line
            var diaLbl = new System.Windows.Controls.TextBlock
            {
                Text = "Ø", Foreground = C("#4FC3F7"),
                FontSize = s * 5, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(diaLbl, s * 5.5); Canvas.SetTop(diaLbl, s * 19);
            Add(diaLbl);

            // ── Right half: ruler ─────────────────────────────────────────────
            // Ruler body (tan/wood tone)
            Add(new Rectangle
            {
                Width = s * 8, Height = s * 28,
                Fill = C("#F5C97A"),
                RadiusX = s * 1, RadiusY = s * 1,
                Stroke = C("#C8971E"), StrokeThickness = s * 0.7
            });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * 22);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], s * 2);

            // Ruler tick marks (5 major ticks)
            double[] tickYs = { 4, 8, 12, 16, 20, 24 };
            foreach (double ty in tickYs)
            {
                bool major = ((int)ty % 8 == 0);
                double tickW = major ? s * 4 : s * 2.5;
                Add(new Line
                {
                    X1 = s * 22, Y1 = s * ty,
                    X2 = s * 22 + tickW, Y2 = s * ty,
                    Stroke = C("#7B5800"), StrokeThickness = s * 0.8
                });
            }

            // "in" label on ruler
            var rulerLbl = new System.Windows.Controls.TextBlock
            {
                Text = "in", Foreground = C("#5D3A00"),
                FontSize = s * 3.5, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(rulerLbl, s * 23); Canvas.SetTop(rulerLbl, s * 26);
            Add(rulerLbl);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Block to Surface icon — 3-D cube + arrow pointing down into surface
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildBlockToSurfaceIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1A1A2E"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Isometric cube (top-left area) ────────────────────────────────
            // Top face (lightest)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*3),   // top
                    new System.Windows.Point(s*25, s*8),   // right
                    new System.Windows.Point(s*16, s*13),  // bottom
                    new System.Windows.Point(s*7,  s*8),   // left
                }),
                Fill = C("#FF8A65")
            });

            // Left face (darker)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*7,  s*8),
                    new System.Windows.Point(s*16, s*13),
                    new System.Windows.Point(s*16, s*22),
                    new System.Windows.Point(s*7,  s*17),
                }),
                Fill = C("#BF360C")
            });

            // Right face (medium)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*25, s*8),
                    new System.Windows.Point(s*25, s*17),
                    new System.Windows.Point(s*16, s*22),
                    new System.Windows.Point(s*16, s*13),
                }),
                Fill = C("#E64A19")
            });

            // Cube edge outlines
            var edgeColor = C("#FF7043");
            double et = s * 0.8;
            // Top face outline
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*3),
                    new System.Windows.Point(s*25, s*8),
                    new System.Windows.Point(s*16, s*13),
                    new System.Windows.Point(s*7,  s*8),
                }),
                Fill = C("Transparent"), Stroke = edgeColor, StrokeThickness = et
            });
            // Vertical left edge
            Add(new Line { X1=s*7, Y1=s*8, X2=s*7, Y2=s*17, Stroke=edgeColor, StrokeThickness=et });
            // Vertical right edge
            Add(new Line { X1=s*25, Y1=s*8, X2=s*25, Y2=s*17, Stroke=edgeColor, StrokeThickness=et });
            // Bottom edges
            Add(new Line { X1=s*7, Y1=s*17, X2=s*16, Y2=s*22, Stroke=edgeColor, StrokeThickness=et });
            Add(new Line { X1=s*25, Y1=s*17, X2=s*16, Y2=s*22, Stroke=edgeColor, StrokeThickness=et });

            // ── Downward arrow below the cube ─────────────────────────────────
            // Arrow shaft
            Add(new Line { X1=s*16, Y1=s*22, X2=s*16, Y2=s*28,
                Stroke=C("#FFCC02"), StrokeThickness=s*2 });
            // Arrowhead
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*16, s*31),  // tip
                    new System.Windows.Point(s*11, s*26),  // left
                    new System.Windows.Point(s*21, s*26),  // right
                }),
                Fill = C("#FFCC02")
            });

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Text to Surface icon — MText label + leader arrow → terrain surface
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildTextToSurfaceIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1A1A2E"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Surface terrain fill (bottom third) ───────────────────────────
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*0,  s*22),
                    new System.Windows.Point(s*7,  s*19),
                    new System.Windows.Point(s*14, s*22),
                    new System.Windows.Point(s*21, s*18),
                    new System.Windows.Point(s*32, s*21),
                    new System.Windows.Point(s*32, s*32),
                    new System.Windows.Point(s*0,  s*32),
                }),
                Fill = C("#1B3A1B")
            });

            // Surface terrain outline (TIN mesh look)
            Add(new Polyline
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*0,  s*22),
                    new System.Windows.Point(s*7,  s*19),
                    new System.Windows.Point(s*14, s*22),
                    new System.Windows.Point(s*21, s*18),
                    new System.Windows.Point(s*32, s*21),
                }),
                Stroke = C("#66BB6A"), StrokeThickness = s * 1.6,
                StrokeLineJoin = PenLineJoin.Round
            });

            // TIN triangle interior lines
            Add(new Line { X1=s*7,  Y1=s*19, X2=s*14, Y2=s*22,
                Stroke=C("#388E3C"), StrokeThickness=s*0.8 });
            Add(new Line { X1=s*14, Y1=s*22, X2=s*21, Y2=s*18,
                Stroke=C("#388E3C"), StrokeThickness=s*0.8 });

            // ── MTEXT box (top-left) ──────────────────────────────────────────
            var frame = new Rectangle
            {
                Width = s * 14, Height = s * 10,
                Stroke = C("#80DEEA"), StrokeThickness = s * 1.3,
                Fill = C("#0D2030"),
                RadiusX = s * 1.5, RadiusY = s * 1.5
            };
            Canvas.SetLeft(frame, s * 1); Canvas.SetTop(frame, s * 2); Add(frame);

            // Text lines inside the box
            Add(new Line { X1=s*3,  Y1=s*5.5, X2=s*13, Y2=s*5.5,
                Stroke=C("#B2EBF2"), StrokeThickness=s*1.2 });
            Add(new Line { X1=s*3,  Y1=s*8,   X2=s*11, Y2=s*8,
                Stroke=C("#80DEEA"), StrokeThickness=s*1.0 });
            Add(new Line { X1=s*3,  Y1=s*10,  X2=s*12, Y2=s*10,
                Stroke=C("#80DEEA"), StrokeThickness=s*1.0 });

            // ── Leader line: shoulder → diagonal → arrowhead tip ─────────────
            var leaderBrush = C("#4DD0E1");
            double lw = s * 1.4;

            // Horizontal shoulder from text box right edge
            Add(new Line { X1=s*15, Y1=s*7, X2=s*20, Y2=s*7,
                Stroke=leaderBrush, StrokeThickness=lw });

            // Diagonal down to surface
            Add(new Line { X1=s*20, Y1=s*7, X2=s*27, Y2=s*18,
                Stroke=leaderBrush, StrokeThickness=lw });

            // Arrowhead at (27, 18) pointing toward surface
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*27, s*19),   // tip
                    new System.Windows.Point(s*24, s*14),   // back-left
                    new System.Windows.Point(s*29, s*14),   // back-right
                }),
                Fill = leaderBrush
            });

            // Elevation number label next to arrowhead (small "7.62")
            var elev = new System.Windows.Controls.TextBlock
            {
                Text = "7.62",
                FontSize = s * 6,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = C("#FFF176")
            };
            Canvas.SetLeft(elev, s * 17); Canvas.SetTop(elev, s * 9); Add(elev);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Section Drawer icon — road cross-section profile + pencil drawing it
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildSectionDrawerIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // ── Background ─────────────────────────────────────────────────────
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1A1A2E"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Road cross-section layers (left ~70 %) ─────────────────────────

            // Earth / subgrade
            var earth = new Rectangle { Width = s*21, Height = s*10, Fill = C("#4E342E") };
            Canvas.SetLeft(earth, s*2); Canvas.SetTop(earth, s*21); Add(earth);

            // Gravel base (tan)
            var grav = new Rectangle { Width = s*16, Height = s*5, Fill = C("#8D6E63") };
            Canvas.SetLeft(grav, s*2); Canvas.SetTop(grav, s*17); Add(grav);

            // Asphalt (dark blue-gray)
            var asp = new Rectangle { Width = s*16, Height = s*5, Fill = C("#546E7A") };
            Canvas.SetLeft(asp, s*2); Canvas.SetTop(asp, s*13); Add(asp);

            // TypeF curb block (concrete gray) — rises above road surface
            var curb = new Rectangle { Width = s*5, Height = s*18, Fill = C("#90A4AE") };
            Canvas.SetLeft(curb, s*18); Canvas.SetTop(curb, s*10); Add(curb);

            // Layer dividers
            Add(new Line { X1=s*2, Y1=s*17, X2=s*18, Y2=s*17,
                Stroke=C("#37474F"), StrokeThickness=s*0.8 });
            Add(new Line { X1=s*2, Y1=s*21, X2=s*23, Y2=s*21,
                Stroke=C("#263238"), StrokeThickness=s*0.7 });

            // ── Cyan surface profile line: road → curb face → curb top ─────────
            double lw = s * 1.4;
            Add(new Line { X1=s*2,  Y1=s*13, X2=s*18, Y2=s*13, Stroke=C("#00BCD4"), StrokeThickness=lw });
            Add(new Line { X1=s*18, Y1=s*13, X2=s*18, Y2=s*10, Stroke=C("#00BCD4"), StrokeThickness=lw });
            Add(new Line { X1=s*18, Y1=s*10, X2=s*23, Y2=s*10, Stroke=C("#00BCD4"), StrokeThickness=lw });

            // ── Centerline tick (yellow dashes, left edge) ─────────────────────
            Add(new Line { X1=s*4, Y1=s*8, X2=s*4, Y2=s*13,
                Stroke=C("#FFCC02"), StrokeThickness=s*1.5,
                StrokeDashArray=new DoubleCollection { 2.0, 1.5 } });

            // ── Pencil (upper-right, tip at curb-top profile, body to corner) ───
            //
            //  Tip       (22, 10)
            //  Side A    (23,  8)   Side B (21, 11)   — near tip
            //  Body end  (29,  4)   end B  (27,  7)   — body/eraser join
            //  Eraser    (30,  2)   end B  (28,  5)
            //
            //  Body parallelogram check: A + endB - B = endA → (23+27-21, 8+7-11) = (29,4) ✓
            //  Eraser parallelogram:     endA + erasB - endB = erasA → (29+28-27, 4+5-7) = (30,2) ✓

            // Tip cone (wood / tan)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*22, s*10),
                    new System.Windows.Point(s*23, s*8),
                    new System.Windows.Point(s*21, s*11),
                }),
                Fill = C("#D7CCC8")
            });

            // Pencil body (yellow)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*23, s*8),
                    new System.Windows.Point(s*21, s*11),
                    new System.Windows.Point(s*27, s*7),
                    new System.Windows.Point(s*29, s*4),
                }),
                Fill = C("#FFC107")
            });

            // Eraser strip (pink)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*29, s*4),
                    new System.Windows.Point(s*27, s*7),
                    new System.Windows.Point(s*28, s*5),
                    new System.Windows.Point(s*30, s*2),
                }),
                Fill = C("#F48FB1")
            });

            // Pencil outline (dark)
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s*22, s*10),
                    new System.Windows.Point(s*23, s*8),
                    new System.Windows.Point(s*29, s*4),
                    new System.Windows.Point(s*30, s*2),
                    new System.Windows.Point(s*28, s*5),
                    new System.Windows.Point(s*27, s*7),
                    new System.Windows.Point(s*21, s*11),
                }),
                Fill = Brushes.Transparent,
                Stroke = C("#5D4037"), StrokeThickness = s * 0.7
            });

            // Graphite dot at tip
            var dot = new Ellipse { Width = s*2, Height = s*2, Fill = C("#263238") };
            Canvas.SetLeft(dot, s*21); Canvas.SetTop(dot, s*9); Add(dot);

            return RenderToBitmap(canvas, size, size);
        }

        private static ImageSource BuildVtParkIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size };
            void Add(System.Windows.UIElement e) => canvas.Children.Add(e);
            Brush C(string hex) => new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));

            var bg = new System.Windows.Shapes.Rectangle
            { Width = size, Height = size, Fill = C("#4A148C"), RadiusX = s*4, RadiusY = s*4 };
            Add(bg);

            // Parking stall lines
            for (int i = 0; i < 4; i++)
            {
                var line = new System.Windows.Shapes.Rectangle
                {
                    Width = s * 1.5, Height = s * 14,
                    Fill = C("#CE93D8")
                };
                Canvas.SetLeft(line, s * (6 + i * 6)); Canvas.SetTop(line, s * 6);
                Add(line);
            }

            // "P" label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "P", Foreground = C("#E1BEE7"),
                FontSize = s * 10, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(label, s * 10); Canvas.SetTop(label, s * 18);
            Add(label);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  EEE Bend icon — side-view profile of a pipe duck
        //  Shows: horizontal pipe, two upper bends (dots), two diagonal legs,
        //  a bottom horizontal run, all on a dark blue background.
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildEeeBendIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0D1B2A"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Pipe path (duck / bypass shape) in profile view ───────────────
            // Layout (all in 32-unit space, scaled by s):
            //   Horizontal run: y=10, x=1..8   (left of duck)
            //   Upper-Left bend:  (8, 10)
            //   Diagonal left:    (8,10) → (10, 22)
            //   Bottom run:       x=10..22, y=22
            //   Diagonal right:   (22,22) → (24, 10)
            //   Upper-Right bend: (24, 10)
            //   Horizontal run:   x=24..31, y=10 (right of duck)

            string pipePath =
                $"M {s*1},{s*10} L {s*8},{s*10} " +
                $"L {s*10},{s*22} " +
                $"L {s*22},{s*22} " +
                $"L {s*24},{s*10} " +
                $"L {s*31},{s*10}";

            // Shadow / outline (slightly thicker, dark)
            Add(new Path
            {
                Data = Geometry.Parse(pipePath),
                Stroke = C("#0A2030"), StrokeThickness = s * 3.5,
                Fill = C("Transparent"), StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });

            // Main pipe stroke (cyan-blue)
            Add(new Path
            {
                Data = Geometry.Parse(pipePath),
                Stroke = C("#29B6F6"), StrokeThickness = s * 2.5,
                Fill = C("Transparent"), StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });

            // ── Upper bend markers (orange dots) ──────────────────────────────
            foreach (var (cx, cy) in new[] { (8.0, 10.0), (24.0, 10.0) })
            {
                Add(new System.Windows.Shapes.Ellipse
                {
                    Width = s * 4, Height = s * 4,
                    Fill = C("#FF8A65"), Stroke = C("#BF360C"), StrokeThickness = s * 0.5
                });
                Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * (cx - 2));
                Canvas.SetTop(canvas.Children[canvas.Children.Count - 1],  s * (cy - 2));
            }

            // ── Lower bend markers (yellow dots) ─────────────────────────────
            foreach (var (cx, cy) in new[] { (10.0, 22.0), (22.0, 22.0) })
            {
                Add(new System.Windows.Shapes.Ellipse
                {
                    Width = s * 4, Height = s * 4,
                    Fill = C("#FFF176"), Stroke = C("#F57F17"), StrokeThickness = s * 0.5
                });
                Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * (cx - 2));
                Canvas.SetTop(canvas.Children[canvas.Children.Count - 1],  s * (cy - 2));
            }

            // ── "Crossing pipe" — orange horizontal bar above the duck ────────
            Add(new Rectangle
            {
                Width = s * 10, Height = s * 3,
                Fill = C("#FF8A65"), Opacity = 0.85,
                RadiusX = s * 1, RadiusY = s * 1
            });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * 11);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1],  s * 5);

            // "X" label on crossing pipe
            var xl = new System.Windows.Controls.TextBlock
            {
                Text = "X", Foreground = C("#0D1B2A"),
                FontSize = s * 4, FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(xl, s * 14.5); Canvas.SetTop(xl, s * 5.2);
            Add(xl);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ALDT Help icon — open book with a question mark
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildAldtHelpIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0D1520"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Book left page ─────────────────────────────────────────────
            Add(new Rectangle { Width = s*12, Height = s*20,
                Fill = C("#1565C0"), RadiusX = s*1, RadiusY = s*1 });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count-1], s*3);
            Canvas.SetTop(canvas.Children[canvas.Children.Count-1],  s*6);

            // ── Book right page ────────────────────────────────────────────
            Add(new Rectangle { Width = s*12, Height = s*20,
                Fill = C("#1976D2"), RadiusX = s*1, RadiusY = s*1 });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count-1], s*17);
            Canvas.SetTop(canvas.Children[canvas.Children.Count-1],  s*6);

            // ── Book spine ────────────────────────────────────────────────
            Add(new Rectangle { Width = s*2, Height = s*20,
                Fill = C("#0D47A1") });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count-1], s*15);
            Canvas.SetTop(canvas.Children[canvas.Children.Count-1],  s*6);

            // ── Lines on left page ─────────────────────────────────────────
            foreach (double ly in new[] { 11.0, 14.0, 17.0, 20.0 })
            {
                Add(new Line { X1 = s*5, Y1 = s*ly, X2 = s*13, Y2 = s*ly,
                    Stroke = C("#60CDFF"), StrokeThickness = s*0.8, Opacity = 0.5 });
            }

            // ── "?" on right page ──────────────────────────────────────────
            var q = new System.Windows.Controls.TextBlock
            {
                Text = "?",
                Foreground = C("#FFD54F"),
                FontSize = s * 13,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(q, s * 19.5);
            Canvas.SetTop(q,  s * 8);
            Add(q);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Profile Off icon — profile view grid with a pipe being removed (X)
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildProfOffIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0D1520"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Profile view grid (light grey lines) ──────────────────────────
            // Horizontal grid lines (3)
            foreach (double y in new[] { 8.0, 16.0, 24.0 })
                Add(new Line { X1 = s*3, Y1 = s*y, X2 = s*29, Y2 = s*y,
                    Stroke = C("#2A3A4A"), StrokeThickness = s * 0.7 });
            // Vertical grid lines (4)
            foreach (double x in new[] { 3.0, 10.3, 17.6, 24.9 })
                Add(new Line { X1 = s*x, Y1 = s*4, X2 = s*x, Y2 = s*28,
                    Stroke = C("#2A3A4A"), StrokeThickness = s * 0.7 });

            // ── Profile view border ────────────────────────────────────────────
            Add(new Rectangle { Width = s*26, Height = s*24,
                Stroke = C("#546E8A"), StrokeThickness = s * 1.0,
                Fill = C("Transparent"), RadiusX = s*1, RadiusY = s*1 });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count-1], s*3);
            Canvas.SetTop(canvas.Children[canvas.Children.Count-1],  s*4);

            // ── Pipe crossing line (cyan) ──────────────────────────────────────
            Add(new Line { X1 = s*3, Y1 = s*19, X2 = s*29, Y2 = s*13,
                Stroke = C("#29B6F6"), StrokeThickness = s * 2.5,
                Opacity = 0.5 });

            // ── Red X over the pipe (marking it as removed) ───────────────────
            double cx = 16, cy = 16, r = 6;
            // X shadow
            Add(new Line { X1 = s*(cx-r), Y1 = s*(cy-r), X2 = s*(cx+r), Y2 = s*(cy+r),
                Stroke = C("#7F0000"), StrokeThickness = s * 3.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            Add(new Line { X1 = s*(cx+r), Y1 = s*(cy-r), X2 = s*(cx-r), Y2 = s*(cy+r),
                Stroke = C("#7F0000"), StrokeThickness = s * 3.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            // X bright
            Add(new Line { X1 = s*(cx-r), Y1 = s*(cy-r), X2 = s*(cx+r), Y2 = s*(cy+r),
                Stroke = C("#EF5350"), StrokeThickness = s * 2.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            Add(new Line { X1 = s*(cx+r), Y1 = s*(cy-r), X2 = s*(cx-r), Y2 = s*(cy+r),
                Stroke = C("#EF5350"), StrokeThickness = s * 2.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  PV Style Override icon — profile view grid with a paint-brush indicator
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildPvStyleIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1A0D2A"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Profile view grid ──────────────────────────────────────────────
            foreach (double y in new[] { 8.0, 16.0, 24.0 })
                Add(new Line { X1 = s*3, Y1 = s*y, X2 = s*29, Y2 = s*y,
                    Stroke = C("#2A3A4A"), StrokeThickness = s * 0.7 });
            foreach (double x in new[] { 3.0, 10.3, 17.6, 24.9 })
                Add(new Line { X1 = s*x, Y1 = s*4, X2 = s*x, Y2 = s*28,
                    Stroke = C("#2A3A4A"), StrokeThickness = s * 0.7 });

            // ── Profile view border ────────────────────────────────────────────
            Add(new Rectangle { Width = s*26, Height = s*24,
                Stroke = C("#546E8A"), StrokeThickness = s * 1.0,
                Fill = C("Transparent"), RadiusX = s*1, RadiusY = s*1 });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count-1], s*3);
            Canvas.SetTop(canvas.Children[canvas.Children.Count-1],  s*4);

            // ── Pipe line (purple-tinted, matching PVSTYLE color) ─────────────
            Add(new Line { X1 = s*3, Y1 = s*20, X2 = s*22, Y2 = s*13,
                Stroke = C("#CE93D8"), StrokeThickness = s * 2.5, Opacity = 0.7 });

            // ── Paint palette circle (style indicator) — bottom-right ─────────
            // Outer circle shadow
            Add(new Ellipse { Width = s*13, Height = s*13,
                Fill = C("#4A1060"), Stroke = C("#CE93D8"), StrokeThickness = s * 1.0 });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count-1], s*18);
            Canvas.SetTop(canvas.Children[canvas.Children.Count-1],  s*17);

            // Three color dots on the palette
            foreach (var (dx, dy, col) in new[] {
                (21.5, 20.5, "#EF5350"),
                (24.5, 22.0, "#FFD54F"),
                (22.0, 25.0, "#4FC3F7") })
            {
                Add(new Ellipse { Width = s*2.2, Height = s*2.2, Fill = C(col) });
                Canvas.SetLeft(canvas.Children[canvas.Children.Count-1], s*dx);
                Canvas.SetTop(canvas.Children[canvas.Children.Count-1],  s*dy);
            }

            // Brush handle line
            Add(new Line { X1 = s*27, Y1 = s*19, X2 = s*30, Y2 = s*16,
                Stroke = C("#CE93D8"), StrokeThickness = s * 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Pressure Count icon — pipe run with numbered fitting markers
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildPressCountIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#1A0A2A"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Horizontal pressure main (cyan-blue pipe) ─────────────────────
            // Shadow
            Add(new Line { X1 = s*2, Y1 = s*16, X2 = s*30, Y2 = s*16,
                Stroke = C("#0A1020"), StrokeThickness = s * 4.5 });
            // Pipe body
            Add(new Line { X1 = s*2, Y1 = s*16, X2 = s*30, Y2 = s*16,
                Stroke = C("#7E57C2"), StrokeThickness = s * 3.5 });
            // Highlight
            Add(new Line { X1 = s*2, Y1 = s*14.8, X2 = s*30, Y2 = s*14.8,
                Stroke = C("#B39DDB"), StrokeThickness = s * 0.8, Opacity = 0.5 });

            // ── Three fitting dots at x = 8, 16, 24 ──────────────────────────
            double[] fxs = { 8, 16, 24 };
            string[] nums = { "1", "2", "3" };
            for (int i = 0; i < fxs.Length; i++)
            {
                double fx = fxs[i];
                // Outer ring (lavender)
                Add(new System.Windows.Shapes.Ellipse
                {
                    Width = s * 6, Height = s * 6,
                    Fill = C("#CE93D8"), Stroke = C("#6A1B9A"), StrokeThickness = s * 0.8
                });
                Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * (fx - 3));
                Canvas.SetTop(canvas.Children[canvas.Children.Count - 1],  s * 13);

                // Number label above fitting
                var lbl = new System.Windows.Controls.TextBlock
                {
                    Text = nums[i],
                    Foreground = C("#F3E5F5"),
                    FontSize = s * 5,
                    FontWeight = System.Windows.FontWeights.Bold
                };
                Canvas.SetLeft(lbl, s * (fx - 1.8));
                Canvas.SetTop(lbl,  s * 4);
                Add(lbl);

                // Tick line from number down to fitting
                Add(new Line
                {
                    X1 = s * fx, Y1 = s * 10,
                    X2 = s * fx, Y2 = s * 13,
                    Stroke = C("#CE93D8"), StrokeThickness = s * 0.7, Opacity = 0.7
                });
            }

            // ── "3D" badge (bottom-right) ─────────────────────────────────────
            Add(new Rectangle
            {
                Width = s * 8, Height = s * 6,
                Fill = C("#4A148C"), RadiusX = s * 1.5, RadiusY = s * 1.5
            });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], s * 22);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1],  s * 23);

            var badge = new System.Windows.Controls.TextBlock
            {
                Text = "3D",
                Foreground = C("#EDE7F6"),
                FontSize = s * 4.5,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(badge, s * 23.2);
            Canvas.SetTop(badge,  s * 23.2);
            Add(badge);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  RR Network Check icon — pipes with clearance arrows
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildRrNetworkCheckIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0a1a0a"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Surface/ground line (wavy terrain at top) ───────────────────
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*2},{s*10} L {s*8},{s*8} L {s*14},{s*11} L {s*20},{s*7} " +
                    $"L {s*26},{s*9} L {s*30},{s*8}"),
                Stroke = C("#66BB6A"), StrokeThickness = s * 2,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            // ── Horizontal pressure pipe (blue) ─────────────────────────────
            Add(new Rectangle
            {
                Width = s * 26, Height = s * 5,
                Fill = C("#1565C0"),
                RadiusX = s * 1.5, RadiusY = s * 1.5
            });
            Canvas.SetLeft(canvas.Children[^1], s * 3); Canvas.SetTop(canvas.Children[^1], s * 19);

            // pipe highlight
            Add(new Rectangle
            {
                Width = s * 26, Height = s * 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(80, 0x90, 0xCA, 0xF9)),
                RadiusX = s * 1, RadiusY = s * 1
            });
            Canvas.SetLeft(canvas.Children[^1], s * 3); Canvas.SetTop(canvas.Children[^1], s * 19);

            // ── Clearance arrow (vertical, between surface and pipe) ─────────
            // Arrow shaft
            Add(new Line
            {
                X1 = s * 16, Y1 = s * 12,
                X2 = s * 16, Y2 = s * 18,
                Stroke = C("#FFEB3B"), StrokeThickness = s * 1.2
            });
            // Top arrowhead
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s * 16, s * 11.5),
                    new System.Windows.Point(s * 14, s * 14),
                    new System.Windows.Point(s * 18, s * 14)
                }),
                Fill = C("#FFEB3B")
            });
            // Bottom arrowhead
            Add(new Polygon
            {
                Points = new System.Windows.Media.PointCollection(new[]
                {
                    new System.Windows.Point(s * 16, s * 18.5),
                    new System.Windows.Point(s * 14, s * 16.5),
                    new System.Windows.Point(s * 18, s * 16.5)
                }),
                Fill = C("#FFEB3B")
            });

            // ── Checkmark badge (bottom-right) ───────────────────────────────
            Add(new Ellipse
            {
                Width = s * 9, Height = s * 9,
                Fill = C("#2E7D32")
            });
            Canvas.SetLeft(canvas.Children[^1], s * 22); Canvas.SetTop(canvas.Children[^1], s * 24);

            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*24},{s*28.5} L {s*26},{s*30.5} L {s*29.5},{s*26}"),
                Stroke = Brushes.White, StrokeThickness = s * 1.5,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ChopChop icon — profile view split into segments with scissors
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildChopChopIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0d1a2d"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Large profile view (source) — top ─────────────────────────────
            // Grid box
            Add(new Rectangle
            {
                Width = s * 24, Height = s * 10,
                Fill = C("#1a2d40"),
                Stroke = C("#4488CC"), StrokeThickness = s * 0.8,
                RadiusX = s * 1, RadiusY = s * 1
            });
            Canvas.SetLeft(canvas.Children[^1], s * 4); Canvas.SetTop(canvas.Children[^1], s * 3);

            // Profile line on source view
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*5},{s*11} L {s*9},{s*7} L {s*14},{s*9} L {s*18},{s*5} " +
                    $"L {s*22},{s*8} L {s*27},{s*6}"),
                Stroke = C("#00AAFF"), StrokeThickness = s * 1.5,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round
            });

            // ── Dashed cut lines (vertical) ───────────────────────────────────
            for (int i = 0; i < 2; i++)
            {
                double cx = s * (13 + i * 7);
                Add(new Line
                {
                    X1 = cx, Y1 = s * 14, X2 = cx, Y2 = s * 20,
                    Stroke = C("#FF6B6B"), StrokeThickness = s * 1.2,
                    StrokeDashArray = new DoubleCollection(new[] { 2.0, 2.0 })
                });
            }

            // ── Three sub-views (bottom row) ──────────────────────────────────
            double[] xs = { 2, 12, 22 };
            string[] colours = { "#4FC3F7", "#66BB6A", "#FFB74D" };
            for (int i = 0; i < 3; i++)
            {
                double bx = s * xs[i];
                double by = s * 21;

                // Sub-view box
                Add(new Rectangle
                {
                    Width = s * 8, Height = s * 7,
                    Fill = C("#1a2d40"),
                    Stroke = C(colours[i]), StrokeThickness = s * 0.8,
                    RadiusX = s * 1, RadiusY = s * 1
                });
                Canvas.SetLeft(canvas.Children[^1], bx); Canvas.SetTop(canvas.Children[^1], by);

                // Mini profile squiggle
                double mx = bx + s * 1;
                Add(new Path
                {
                    Data = Geometry.Parse(
                        $"M {mx},{s*26} L {mx + s*2},{s*23} L {mx + s*4},{s*25} L {mx + s*6},{s*22}"),
                    Stroke = C(colours[i]), StrokeThickness = s * 1,
                    Fill = Brushes.Transparent,
                    StrokeLineJoin = PenLineJoin.Round
                });
            }

            // ── Scissors glyph (small, top-right corner) ─────────────────────
            var scissor = new System.Windows.Controls.TextBlock
            {
                Text = "✂",
                Foreground = C("#FF6B6B"),
                FontSize = s * 7,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(scissor, s * 24); Canvas.SetTop(scissor, s * 12);
            Add(scissor);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Label Gen icon — profile view grid with a crossing pipe and label
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildLLabelGenIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0d1a2d"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Profile view grid
            Add(new Rectangle
            {
                Width = s * 24, Height = s * 16,
                Fill = C("#1a2d40"),
                Stroke = C("#4488CC"), StrokeThickness = s * 1,
                RadiusX = s * 1, RadiusY = s * 1
            });
            Canvas.SetLeft(canvas.Children[^1], s * 4); Canvas.SetTop(canvas.Children[^1], s * 8);

            // Crossing pipe (circle)
            Add(new Ellipse
            {
                Width = s * 6, Height = s * 6,
                Stroke = C("#FF9800"), StrokeThickness = s * 1.5,
                Fill = C("#331a00")
            });
            Canvas.SetLeft(canvas.Children[^1], s * 13); Canvas.SetTop(canvas.Children[^1], s * 13);

            // Label line
            Add(new Line
            {
                X1 = s * 16, Y1 = s * 13, X2 = s * 16, Y2 = s * 6,
                Stroke = C("#00E676"), StrokeThickness = s * 1
            });

            // Label text box
            Add(new Rectangle
            {
                Width = s * 14, Height = s * 6,
                Fill = C("#00E676"),
                RadiusX = s * 1, RadiusY = s * 1
            });
            Canvas.SetLeft(canvas.Children[^1], s * 9); Canvas.SetTop(canvas.Children[^1], s * 2);

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Lateral Manager icon
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildLatManIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#0d1a2d"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Small profile grid
            Add(new Rectangle
            {
                Width = s * 22, Height = s * 14,
                Fill = C("#1a2d40"),
                Stroke = C("#4DB6AC"), StrokeThickness = s * 1,
                RadiusX = s * 1, RadiusY = s * 1
            });
            Canvas.SetLeft(canvas.Children[^1], s * 5); Canvas.SetTop(canvas.Children[^1], s * 4);

            // Ellipse (Lateral Crossing)
            Add(new Ellipse
            {
                Width = s * 10, Height = s * 6,
                Stroke = C("#FFB74D"), StrokeThickness = s * 1.5,
                Fill = C("#663300")
            });
            Canvas.SetLeft(canvas.Children[^1], s * 11); Canvas.SetTop(canvas.Children[^1], s * 8);

            // Target scope overlay
            Add(new Ellipse
            {
                Width = s * 18, Height = s * 18,
                Stroke = C("#00E676"), StrokeThickness = s * 1.5,
                StrokeDashArray = new DoubleCollection(new[] { 2.0, 2.0 })
            });
            Canvas.SetLeft(canvas.Children[^1], s * 7); Canvas.SetTop(canvas.Children[^1], s * 12);

            return RenderToBitmap(canvas, size, size);
        }
        // ═════════════════════════════════════════════════════════════════════
        //  Property Appraisal Lookup icon
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildPropertyAppraisalIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Dark blue background
            Add(new Rectangle { Width = size, Height = size, Fill = C("#122A42"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // Document paper
            Add(new Path
            {
                Data = Geometry.Parse($"M {s*6},{s*4} L {s*20},{s*4} L {s*26},{s*10} L {s*26},{s*28} L {s*6},{s*28} Z"),
                Fill = C("#E0E0E0")
            });
            // Fold
            Add(new Path
            {
                Data = Geometry.Parse($"M {s*20},{s*4} L {s*20},{s*10} L {s*26},{s*10} Z"),
                Fill = C("#BDBDBD")
            });

            // Lines on document
            Add(new Rectangle { Width = s * 12, Height = s * 2, Fill = C("#757575"),
                Margin = new System.Windows.Thickness(s*9, s*12, 0, 0) });
            Add(new Rectangle { Width = s * 14, Height = s * 2, Fill = C("#757575"),
                Margin = new System.Windows.Thickness(s*9, s*16, 0, 0) });
            Add(new Rectangle { Width = s * 10, Height = s * 2, Fill = C("#757575"),
                Margin = new System.Windows.Thickness(s*9, s*20, 0, 0) });

            // Magnifying glass over it
            Add(new Ellipse { Width = s*10, Height = s*10, Stroke = C("#4FC3F7"),
                StrokeThickness = s*2, Margin = new System.Windows.Thickness(s*14, s*16, 0, 0) });
            Add(new Path
            {
                Data = Geometry.Parse($"M {s*22},{s*24} L {s*28},{s*30}"),
                Stroke = C("#4FC3F7"), StrokeThickness = s*3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });

            return RenderToBitmap(canvas, size, size);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Cover Adjust icon — ground surface, two pipes at depth, cover arrows
        //  Concept: brown ground line at top, two small pipe circles below it,
        //  cyan double-headed arrows showing cover, target depth dashed line.
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildCoverAdjustIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background — deep blue-green
            Add(new Rectangle { Width = size, Height = size, Fill = C("#071820"),
                RadiusX = s * 3, RadiusY = s * 3 });

            // ── Ground surface (wavy brown fill) ──────────────────────────────
            // filled earth shape
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*0},{s*0} L {s*32},{s*0} L {s*32},{s*10} " +
                    $"Q {s*24},{s*8} {s*16},{s*10} Q {s*8},{s*12} {s*0},{s*10} Z"),
                Fill = C("#5C3A1E")
            });
            // surface grass line
            Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {s*0},{s*10} Q {s*8},{s*12} {s*16},{s*10} Q {s*24},{s*8} {s*32},{s*10}"),
                Stroke = C("#8BC34A"), StrokeThickness = s * 1.8,
                Fill = Brushes.Transparent, StrokeLineJoin = PenLineJoin.Round
            });

            // ── Target depth line (dashed, cyan dim) ─────────────────────────
            Add(new Line
            {
                X1 = s * 1, Y1 = s * 25, X2 = s * 31, Y2 = s * 25,
                Stroke = new SolidColorBrush(Color.FromArgb(140, 0, 188, 212)),
                StrokeThickness = s * 0.9,
                StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.0 })
            });

            // ── Fitting 1 (left) — pipe circle, originally deeper ─────────────
            double p1x = s * 9, p1y = s * 22, pr = s * 3.2;
            Add(new Ellipse { Width = pr*2, Height = pr*2, Fill = C("#607D8B"),
                Stroke = C("#90A4AE"), StrokeThickness = s * 0.8 });
            Canvas.SetLeft(canvas.Children[^1], p1x - pr);
            Canvas.SetTop(canvas.Children[^1],  p1y - pr);
            // highlight
            Add(new Ellipse { Width = pr * 0.9, Height = pr * 0.9,
                Fill = new SolidColorBrush(Color.FromArgb(80, 200, 230, 255)) });
            Canvas.SetLeft(canvas.Children[^1], p1x - pr * 0.6);
            Canvas.SetTop(canvas.Children[^1],  p1y - pr * 0.8);

            // ── Fitting 2 (right) — pipe circle, originally shallower ─────────
            double p2x = s * 23, p2y = s * 22, p2r = s * 3.2;
            Add(new Ellipse { Width = p2r*2, Height = p2r*2, Fill = C("#607D8B"),
                Stroke = C("#90A4AE"), StrokeThickness = s * 0.8 });
            Canvas.SetLeft(canvas.Children[^1], p2x - p2r);
            Canvas.SetTop(canvas.Children[^1],  p2y - p2r);
            Add(new Ellipse { Width = p2r * 0.9, Height = p2r * 0.9,
                Fill = new SolidColorBrush(Color.FromArgb(80, 200, 230, 255)) });
            Canvas.SetLeft(canvas.Children[^1], p2x - p2r * 0.6);
            Canvas.SetTop(canvas.Children[^1],  p2y - p2r * 0.8);

            // ── Cover arrows: surface crown → fitting crown, both sides ────────
            // Arrow helper: vertical line + arrowhead at bottom
            void CoverArrow(double cx, double topY, double botY, string hexCol)
            {
                Add(new Line { X1 = s*cx, Y1 = topY, X2 = s*cx, Y2 = botY,
                    Stroke = C(hexCol), StrokeThickness = s * 1.2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap   = PenLineCap.Flat });
                // arrowhead down
                Add(new Polygon
                {
                    Points = new System.Windows.Media.PointCollection(new[]
                    {
                        new System.Windows.Point(s*cx,        botY + s*2),
                        new System.Windows.Point(s*cx - s*1.5, botY - s*1),
                        new System.Windows.Point(s*cx + s*1.5, botY - s*1)
                    }),
                    Fill = C(hexCol)
                });
                // tick at top
                Add(new Line { X1 = s*cx - s*1.5, Y1 = topY, X2 = s*cx + s*1.5, Y2 = topY,
                    Stroke = C(hexCol), StrokeThickness = s * 1.2 });
            }

            CoverArrow(9,  s * 11, p1y - pr - s*0.5, "#00BCD4");
            CoverArrow(23, s * 11, p2y - p2r - s*0.5, "#00BCD4");

            // ── "4'" label centred between the arrows ─────────────────────────
            var lbl = new System.Windows.Controls.TextBlock
            {
                Text       = "4'",
                Foreground = C("#00BCD4"),
                FontSize   = s * 5,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(lbl, s * 13.5);
            Canvas.SetTop(lbl,  s * 13.5);
            Add(lbl);

            // ── Equal sign below the pipes (= same elevation) ─────────────────
            foreach (double dy in new[] { 0.0, s * 2.2 })
            {
                Add(new Line
                {
                    X1 = s * 12.5, Y1 = s * 28 + dy, X2 = s * 19.5, Y2 = s * 28 + dy,
                    Stroke = new SolidColorBrush(Color.FromArgb(200, 0, 188, 212)),
                    StrokeThickness = s * 1.2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap   = PenLineCap.Round
                });
            }

            return RenderToBitmap(canvas, size, size);
        }


        // ═════════════════════════════════════════════════════════════════════
        //  Coral As-Built icon — sewer pipe network with manholes
        //  Cyan gravity main, red force main, yellow manhole dot.
        // ═════════════════════════════════════════════════════════════════════
        private static ImageSource BuildCoralAsBuiltIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background — dark navy
            Add(new Rectangle
            {
                Width = size, Height = size,
                Fill = C("#1A1A2E"),
                RadiusX = s * 3, RadiusY = s * 3
            });

            // ── Gravity main — cyan horizontal pipe ───────────────────────────
            // Top line
            Add(new Line
            {
                X1 = s * 3, Y1 = s * 10, X2 = s * 29, Y2 = s * 10,
                Stroke = C("#00BCD4"), StrokeThickness = s * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round
            });
            // Bottom line (gravity main has double line)
            Add(new Line
            {
                X1 = s * 3, Y1 = s * 13, X2 = s * 29, Y2 = s * 13,
                Stroke = C("#00BCD4"), StrokeThickness = s * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round
            });

            // ── Force main — red diagonal pipe ────────────────────────────────
            Add(new Line
            {
                X1 = s * 3, Y1 = s * 22, X2 = s * 29, Y2 = s * 22,
                Stroke = C("#F44336"), StrokeThickness = s * 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                StrokeDashArray    = new System.Windows.Media.DoubleCollection(new double[] { 3, 1.5 })
            });

            // ── Manhole circles (yellow) — at gravity main junctions ──────────
            foreach (double mx in new[] { s * 10.5, s * 21 })
            {
                // Drop line connecting gravity main to manhole
                Add(new Line
                {
                    X1 = mx, Y1 = s * 13, X2 = mx, Y2 = s * 19,
                    Stroke = C("#00BCD4"), StrokeThickness = s * 1.2
                });
                // Manhole circle
                var mh = new Ellipse
                {
                    Width  = s * 5, Height = s * 5,
                    Fill   = C("#FFC107"),
                    Stroke = C("#FFD54F"), StrokeThickness = s * 0.8
                };
                Canvas.SetLeft(mh, mx - s * 2.5);
                Canvas.SetTop(mh,  s * 19);
                Add(mh);
            }

            // ── "AB" label — small text in bottom-right corner ────────────────
            var lbl = new System.Windows.Controls.TextBlock
            {
                Text       = "AB",
                Foreground = C("#B0BEC5"),
                FontSize   = s * 5,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(lbl, s * 20);
            Canvas.SetTop(lbl,  s * 24.5);
            Add(lbl);

            return RenderToBitmap(canvas, size, size);
        }

        private static ImageSource BuildTableDrawIcon(int size)
        {
            double s = size / 32.0;
            var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
            SolidColorBrush C(string hex)
                => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            void Add(System.Windows.UIElement el) => canvas.Children.Add(el);

            // Background — dark green
            Add(new Rectangle
            {
                Width = size, Height = size,
                Fill = C("#1A2E1A"),
                RadiusX = s * 3, RadiusY = s * 3
            });

            // Table grid — 3×3 cells
            double left   = s * 3;
            double top    = s * 4;
            double right  = s * 29;
            double bottom = s * 28;
            double midX   = s * 14;
            double midX2  = s * 21;
            double midY   = s * 14;
            double midY2  = s * 21;

            var gridStroke = C("#4CAF50");
            double gt = s * 1.2;

            // Outer border
            Add(new Rectangle { Width = right - left, Height = bottom - top,
                Stroke = C("#6BCB77"), StrokeThickness = s * 1.5, Fill = Brushes.Transparent,
                RadiusX = s * 1, RadiusY = s * 1 });
            Canvas.SetLeft(canvas.Children[^1], left);
            Canvas.SetTop(canvas.Children[^1],  top);

            // Vertical lines
            Add(new Line { X1 = midX,  Y1 = top, X2 = midX,  Y2 = bottom, Stroke = gridStroke, StrokeThickness = gt });
            Add(new Line { X1 = midX2, Y1 = top, X2 = midX2, Y2 = bottom, Stroke = gridStroke, StrokeThickness = gt });

            // Horizontal lines
            Add(new Line { X1 = left, Y1 = midY,  X2 = right, Y2 = midY,  Stroke = gridStroke, StrokeThickness = gt });
            Add(new Line { X1 = left, Y1 = midY2, X2 = right, Y2 = midY2, Stroke = gridStroke, StrokeThickness = gt });

            // Header row fill (top row, lighter)
            Add(new Rectangle { Width = right - left, Height = midY - top,
                Fill = C("#2E4A2E") });
            Canvas.SetLeft(canvas.Children[^1], left);
            Canvas.SetTop(canvas.Children[^1],  top);

            // Link icon in one cell — small chain symbol (bottom-right cell)
            var lbl = new System.Windows.Controls.TextBlock
            {
                Text       = "⛓",
                Foreground = C("#60CDFF"),
                FontSize   = s * 6,
                FontWeight = System.Windows.FontWeights.Normal
            };
            Canvas.SetLeft(lbl, midX2 + s * 1.5);
            Canvas.SetTop(lbl,  midY2 + s * 1.5);
            Add(lbl);

            // "T" label — top-left cell
            var t = new System.Windows.Controls.TextBlock
            {
                Text       = "T",
                Foreground = C("#6BCB77"),
                FontSize   = s * 6,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(t, left + s * 2);
            Canvas.SetTop(t,  top + s * 1.5);
            Add(t);

            // Row count badge bottom
            var rowLbl = new System.Windows.Controls.TextBlock
            {
                Text       = "3×3",
                Foreground = C("#9E9E9E"),
                FontSize   = s * 4.5,
                FontWeight = System.Windows.FontWeights.Bold
            };
            Canvas.SetLeft(rowLbl, left + s * 2);
            Canvas.SetTop(rowLbl,  bottom + s * 1);
            Add(rowLbl);

            return RenderToBitmap(canvas, size, size);
        }
    }
}

