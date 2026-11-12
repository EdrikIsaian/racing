using System.Numerics;

// A computer driver: aims at a point a little way down the track and
// brakes early enough to take the next corner.
class AiDriver
{
    public float Skill;       // ~0.8 (easy) .. 1.0 (very fast)
    public float LaneOffset;  // drives slightly left/right of the centre line so cars spread out
    float stuckTimer;
    float reverseTimer;

    public AiDriver(float skill, float laneOffset)
    {
        Skill = skill;
        LaneOffset = laneOffset;
    }

    public DriverInput GetInput(Racer me, Track track, float dt)
    {
        Movement m = me.Movement;
        Car car = me.Car;
        float speed = m.ForwardSpeed;
        var input = new DriverInput();

        // Stuck (e.g. nose into another car)? Back up for a moment.
        if (reverseTimer > 0)
        {
            reverseTimer -= dt;
            input.Brake = 1;
            input.Steer = -SteerToward(m, LookAheadPoint(me, track, 6));
            return input;
        }
        stuckTimer = MathF.Abs(speed) < 25f ? stuckTimer + dt : 0;
        if (stuckTimer > 1.5f)
        {
            stuckTimer = 0;
            reverseTimer = 0.9f;
        }

        // Steering: look further ahead the faster we go
        int look = 4 + (int)(MathF.Max(speed, 0) / 110f);
        input.Steer = SteerToward(m, LookAheadPoint(me, track, look));

        // Speed: the fastest we can go now and still slow down in time for every bend ahead
        float topSpeed = car.MaxSpeed * Movement.PixelsPerKmh;
        float baseTurn = Movement.BaseTurnRate(car);
        float slowdownAtTop = 0.35f / topSpeed;
        float allowed = topSpeed * Skill;
        for (int k = 1; k < 70; k++)
        {
            float curv = track.CurvatureAt(me.TrackIndex + k);
            if (curv < 0.0004f) continue;
            float cornerSpeed = Skill * 0.92f * baseTurn / (curv + baseTurn * slowdownAtTop);
            float dist = k * Track.Spacing;
            float reachable = MathF.Sqrt(cornerSpeed * cornerSpeed + 2f * Movement.BrakeDecel * 0.55f * dist);
            allowed = MathF.Min(allowed, reachable);
        }

        if (speed < allowed) input.Throttle = 1;
        else if (speed > allowed + 30f) input.Brake = Math.Clamp((speed - allowed) / 120f, 0.3f, 1f);
        return input;
    }

    Vector2 LookAheadPoint(Racer me, Track track, int samplesAhead)
    {
        int i = me.TrackIndex + samplesAhead;
        return track.PointAt(i) + track.NormalAt(i) * LaneOffset;
    }

    static float SteerToward(Movement m, Vector2 target)
    {
        Vector2 d = target - m.Position;
        float diff = Track.WrapAngle(MathF.Atan2(d.Y, d.X) - m.Heading);
        return Math.Clamp(diff * 2.2f, -1f, 1f);
    }
}
