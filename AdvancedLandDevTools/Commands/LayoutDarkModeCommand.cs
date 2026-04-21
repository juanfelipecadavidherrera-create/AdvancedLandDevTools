// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.LayoutDarkModeCommand))]

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// LAYOUTDARK — Toggles layout (paper space) dark mode on/off.
    ///
    /// State is detected from the ACTUAL current background color on every call,
    /// so the toggle works correctly even when Civil 3D is reopened while dark
    /// (Preferences are global and persist across sessions).
    ///
    /// OFF path:  if we saved originals this session → restore them exactly.
    ///            if the plugin reloaded while dark   → restore AutoCAD defaults
    ///            (white bg, paper ON, shadow ON).
    /// </summary>
    public class LayoutDarkModeCommand
    {
        // OLE COLORREF for the dark background we apply: RGB(33, 33, 33)
        private const uint DarkBg    = 33u | (33u << 8) | (33u << 16);
        // AutoCAD default layout background: RGB(255, 255, 255)
        private const uint DefaultBg = 255u | (255u << 8) | (255u << 16);

        // Saved originals — null means we haven't saved anything this session.
        private static uint? _savedBg;
        private static bool  _savedPaper  = true;
        private static bool  _savedShadow = true;

        [CommandMethod("LAYOUTDARK", CommandFlags.Modal)]
        public void ToggleLayoutDark()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                // Preferences.Display is a COM object; two-step dynamic dispatch is required.
                dynamic prefs   = AcApp.Preferences;
                dynamic display = prefs.Display;

                // Read the ACTUAL current color — don't trust any static flag.
                uint currentBg    = (uint)display.GraphicsWinLayoutBackgrndColor;
                bool currentlyDark = currentBg == DarkBg;

                if (!currentlyDark)
                {
                    // Save what's currently set so we can restore it exactly.
                    _savedBg     = currentBg;
                    _savedPaper  = (bool)display.LayoutDisplayPaper;
                    _savedShadow = (bool)display.LayoutDisplayPaperShadow;

                    display.GraphicsWinLayoutBackgrndColor = DarkBg;
                    display.LayoutDisplayPaper             = false;
                    display.LayoutDisplayPaperShadow       = false;

                    ed.WriteMessage("\nLayout dark mode ON.");
                }
                else
                {
                    // Restore: use saved originals if available, otherwise AutoCAD defaults.
                    display.GraphicsWinLayoutBackgrndColor = _savedBg ?? DefaultBg;
                    display.LayoutDisplayPaper             = _savedBg.HasValue ? _savedPaper  : true;
                    display.LayoutDisplayPaperShadow       = _savedBg.HasValue ? _savedShadow : true;

                    _savedBg = null; // clear so next ON captures fresh originals
                    ed.WriteMessage("\nLayout dark mode OFF — colors restored.");
                }

                ed.UpdateScreen();
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ALDT ERROR] LAYOUTDARK: {ex.Message}\n");
            }
        }
    }
}
