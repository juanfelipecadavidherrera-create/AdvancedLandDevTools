using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AdvancedLandDevTools.VehicleTracking.Core
{
    /// <summary>Parking angle preset.</summary>
    public enum ParkingAngle
    {
        Parallel = 0,
        Angle30 = 30,
        Angle45 = 45,
        Angle60 = 60,
        Perpendicular = 90
    }

    /// <summary>
    /// Dimensional standards for a parking configuration.
    /// All dimensions in feet.
    /// </summary>
    public class ParkingDimensions
    {
        [JsonPropertyName("angle")]
        public ParkingAngle Angle { get; set; }

        [JsonPropertyName("stallWidth")]
        public double StallWidth { get; set; } = 9.0;

        [JsonPropertyName("stallDepth")]
        public double StallDepth { get; set; } = 18.5;

        [JsonPropertyName("aisleWidthOneWay")]
        public double AisleWidthOneWay { get; set; } = 24.0;

        [JsonPropertyName("aisleWidthTwoWay")]
        public double AisleWidthTwoWay { get; set; } = 24.0;
    }

    /// <summary>ADA accessible parking requirements.</summary>
    public class AdaRequirements
    {
        /// <summary>Standard accessible space width (ft).</summary>
        public double StandardWidth { get; set; } = 8.0;

        /// <summary>Standard access aisle width (ft).</summary>
        public double StandardAisle { get; set; } = 5.0;

        /// <summary>Van-accessible space width (ft).</summary>
        public double VanWidth { get; set; } = 11.0;

        /// <summary>Van access aisle width (ft).</summary>
        public double VanAisle { get; set; } = 5.0;

        /// <summary>Van-accessible vertical clearance (inches).</summary>
        public double VanClearanceInches { get; set; } = 98.0;

        /// <summary>Maximum slope in any direction (ratio).</summary>
        public double MaxSlope { get; set; } = 0.02; // 2% = 1:48
    }

    /// <summary>A single parking stall polygon.</summary>
    public class ParkingStall
    {
        /// <summary>Four corners of the stall: FL, FR, RR, RL.</summary>
        public Vec2[] Corners { get; set; } = Array.Empty<Vec2>();

        /// <summary>Whether this is an ADA accessible stall.</summary>
        public bool IsAccessible { get; set; }

        /// <summary>Whether this is a van-accessible ADA stall.</summary>
        public bool IsVanAccessible { get; set; }

        /// <summary>Center point of the stall.</summary>
        public Vec2 Center { get; set; }
    }

    /// <summary>Result of the parking layout generator.</summary>
    public class ParkingLayoutResult
    {
        public List<ParkingStall> Stalls { get; set; } = new();
        public List<Vec2[]> AislePolylines { get; set; } = new();
        public int TotalRegularSpaces { get; set; }
        public int TotalAccessibleSpaces { get; set; }
        public int TotalVanAccessibleSpaces { get; set; }
        public double TotalArea { get; set; }
    }

    /// <summary>
    /// Generates parking layouts within a boundary polygon.
    /// </summary>
    public class ParkingLayoutGenerator
    {
        public ParkingDimensions Dimensions { get; set; } = new();
        public AdaRequirements Ada { get; set; } = new();
        public int AdaSpacesRequired { get; set; } = 1;
        public bool TwoWayAisle { get; set; } = true;

        /// <summary>
        /// Generate a parking layout within a rectangular boundary.
        /// </summary>
        /// <param name="origin">Bottom-left corner of the boundary.</param>
        /// <param name="width">Boundary width (ft).</param>
        /// <param name="height">Boundary height (ft).</param>
        /// <param name="rowDirection">Direction angle of rows (radians, 0 = east).</param>
        public ParkingLayoutResult Generate(Vec2 origin, double width, double height, double rowDirection = 0)
        {
            var result = new ParkingLayoutResult();
            result.TotalArea = width * height;

            double aisleW = TwoWayAisle ? Dimensions.AisleWidthTwoWay : Dimensions.AisleWidthOneWay;
            double stallD = Dimensions.StallDepth;
            double stallW = Dimensions.StallWidth;
            double angleRad = (int)Dimensions.Angle * Math.PI / 180.0;

            // Module width = stall depth + aisle + stall depth (double-loaded row)
            double moduleWidth = stallD + aisleW + stallD;

            // Effective stall width along the row (accounts for parking angle)
            double effectiveStallWidth = Dimensions.Angle == ParkingAngle.Perpendicular
                ? stallW
                : stallW / Math.Sin(Math.Max(angleRad, 0.01));

            if (Dimensions.Angle == ParkingAngle.Parallel)
                effectiveStallWidth = Dimensions.StallDepth + 2; // parallel: length along curb

            double cosDir = Math.Cos(rowDirection);
            double sinDir = Math.Sin(rowDirection);
            double perpX = -sinDir; // perpendicular to row direction
            double perpY = cosDir;

            // Place rows across the width
            int rowPairs = (int)(height / moduleWidth);
            int adaPlaced = 0;

            for (int rp = 0; rp < rowPairs; rp++)
            {
                double rowBaseOffset = rp * moduleWidth;

                // Two rows per module (each side of aisle)
                for (int side = 0; side < 2; side++)
                {
                    double stallBaseOffset = side == 0
                        ? rowBaseOffset
                        : rowBaseOffset + stallD + aisleW;

                    // Number of stalls along the row length
                    int count = (int)(width / effectiveStallWidth);

                    for (int s = 0; s < count; s++)
                    {
                        double alongPos = s * effectiveStallWidth;
                        bool isAda = adaPlaced < AdaSpacesRequired && s == count - 1 && side == 0 && rp == 0;

                        double sw = isAda ? Ada.StandardWidth : stallW;
                        double sd = stallD;

                        // Stall corners
                        Vec2 bl = new Vec2(
                            origin.X + alongPos * cosDir + stallBaseOffset * perpX,
                            origin.Y + alongPos * sinDir + stallBaseOffset * perpY);
                        Vec2 br = new Vec2(bl.X + sw * cosDir, bl.Y + sw * sinDir);
                        Vec2 tr = new Vec2(br.X + sd * perpX, br.Y + sd * perpY);
                        Vec2 tl = new Vec2(bl.X + sd * perpX, bl.Y + sd * perpY);

                        var stall = new ParkingStall
                        {
                            Corners = new[] { bl, br, tr, tl },
                            Center = new Vec2((bl.X + tr.X) * 0.5, (bl.Y + tr.Y) * 0.5),
                            IsAccessible = isAda,
                            IsVanAccessible = false
                        };

                        result.Stalls.Add(stall);
                        if (isAda) adaPlaced++;
                    }
                }
            }

            result.TotalRegularSpaces = result.Stalls.Count - adaPlaced;
            result.TotalAccessibleSpaces = adaPlaced;
            return result;
        }
    }
}
