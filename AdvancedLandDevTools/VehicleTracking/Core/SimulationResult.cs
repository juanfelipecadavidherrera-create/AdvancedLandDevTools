using System;
using System.Collections.Generic;

namespace AdvancedLandDevTools.VehicleTracking.Core
{
    /// <summary>
    /// A 2D point used throughout the VT core (no AutoCAD dependency).
    /// </summary>
    public readonly struct Vec2
    {
        public readonly double X;
        public readonly double Y;

        public Vec2(double x, double y) { X = x; Y = y; }

        public double DistanceTo(Vec2 other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

        public override string ToString() => $"({X:F3}, {Y:F3})";
    }

    /// <summary>
    /// Snapshot of a vehicle's state at one simulation step.
    /// </summary>
    public class VehicleSnapshot
    {
        /// <summary>Station along the path (ft from start).</summary>
        public double Station { get; set; }

        /// <summary>Front axle center position.</summary>
        public Vec2 FrontAxle { get; set; }

        /// <summary>Rear axle center position.</summary>
        public Vec2 RearAxle { get; set; }

        /// <summary>Vehicle body heading (radians, 0 = east, CCW positive).</summary>
        public double Heading { get; set; }

        /// <summary>Current steering angle (radians, positive = left).</summary>
        public double SteeringAngle { get; set; }

        /// <summary>Four body corners: FL, FR, RR, RL.</summary>
        public Vec2[] BodyCorners { get; set; } = Array.Empty<Vec2>();

        /// <summary>For articulated vehicles: snapshots of each trailer unit.</summary>
        public TrailerSnapshot[] TrailerSnapshots { get; set; } = Array.Empty<TrailerSnapshot>();
    }

    /// <summary>Snapshot of a single trailer unit at one simulation step.</summary>
    public class TrailerSnapshot
    {
        public Vec2 FrontHitch { get; set; }
        public Vec2 RearAxle { get; set; }
        public double Heading { get; set; }
        public Vec2[] BodyCorners { get; set; } = Array.Empty<Vec2>();
    }

    /// <summary>A collision detected during simulation.</summary>
    public class CollisionHit
    {
        public double Station { get; set; }
        public Vec2 Location { get; set; }
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Complete result of a swept path simulation.
    /// </summary>
    public class SimulationResult
    {
        /// <summary>Ordered snapshots at each simulation step.</summary>
        public List<VehicleSnapshot> Snapshots { get; set; } = new();

        /// <summary>Outer swept envelope polygon (closed loop of Vec2).</summary>
        public List<Vec2> OuterEnvelope { get; set; } = new();

        /// <summary>Inner wheel path (innermost wheel trace).</summary>
        public List<Vec2> InnerWheelPath { get; set; } = new();

        /// <summary>Outer wheel path (outermost wheel trace).</summary>
        public List<Vec2> OuterWheelPath { get; set; } = new();

        /// <summary>Maximum swept width encountered (ft).</summary>
        public double MaxSweptWidth { get; set; }

        /// <summary>Maximum offtracking (ft).</summary>
        public double MaxOfftracking { get; set; }

        /// <summary>Collisions detected against design geometry.</summary>
        public List<CollisionHit> Collisions { get; set; } = new();

        /// <summary>Whether the vehicle's steering was clamped at any point.</summary>
        public bool SteeringClamped { get; set; }

        /// <summary>Total path length simulated (ft).</summary>
        public double PathLength { get; set; }
    }
}
