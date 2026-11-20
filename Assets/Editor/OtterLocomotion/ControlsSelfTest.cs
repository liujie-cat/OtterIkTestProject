using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Plays scripted key and mouse input through ControlGestures and GearMachine against a body held still facing +z,
    /// its head at Head, and checks each rule of the controls (行动操作). Writes Logs/Controls_SelfTest.txt; needs no
    /// scene and no Play.
    /// </summary>
    static class ControlsSelfTest
    {
        const float Frame = 1f / 60f;
        static readonly Vector3 Head = new Vector3(0f, 0f, 0.2f);

        class Sim
        {
            public readonly GestureSettings gs = new GestureSettings();
            public readonly GearSettings settings = new GearSettings();
            public readonly GearMachine gears = new GearMachine();
            public readonly List<ControlEvent> events = new List<ControlEvent>();
            readonly ControlGestures input = new ControlGestures();
            readonly List<Gesture> gestures = new List<Gesture>();
            public float time, speedBL;
            public bool airborne;
            public Vector3 point = new Vector3(0f, 0f, 1f);

            public Sim(Gear start = Gear.Park) => gears.Reset(Body, start);

            GearBody Body => new GearBody { position = Vector3.zero, head = Head, forward = Vector3.forward, speedBL = speedBL, airborne = airborne };

            public Sim Run(float seconds, bool click = false, bool go = false, bool back = false, bool onOtter = false)
            {
                for (float t = 0f; t < seconds - 1e-5f; t += Frame)
                {
                    gestures.Clear();
                    Vector3 at = onOtter ? Vector3.zero : point;
                    input.Step(gs, time, click, go, back, at, onOtter, gestures);
                    gears.Step(settings, gestures, time, at, onOtter, Body, events);
                    time += Frame;
                }
                return this;
            }

            public Sim Wait(float seconds) => Run(seconds);
            public Sim Click() => Run(0.1f, click: true).Run(Frame);
            public Sim Go() => Run(0.1f, go: true).Run(Frame);
            public Sim HoldGo(float seconds) => Run(seconds, go: true).Run(Frame);
            public Sim Back() => Run(0.1f, back: true).Run(Frame);

            public Sim Guide(float seconds, Vector3 to)
            {
                point = to;
                return Run(seconds);
            }

            public int Kicks => events.Count(e => e.kind == ControlEventKind.Kick);
            public int Jumps => events.Count(e => e.kind == ControlEventKind.Jump);
            public bool Supine => gears.Posture == Posture.Supine;
            public override string ToString() =>
                $"{(gears.Selected ? "selected" : "not selected")}, gear {gears.Gear}{(gears.Dolphin ? " wave" : "")}, {gears.Posture}, {gears.Gait}, {gears.SpeedBL:F2} BL/s; " +
                $"events [{string.Join(", ", events)}]";
        }

        // sprinting in the wave, the go key still held
        static Sim InWave()
        {
            var s = new Sim(Gear.Cruise) { speedBL = 1.9f };
            return s.Run(0.4f, go: true);
        }

        [MenuItem("Tools/Otter Locomotion/Self-Test Controls")]
        static void Run()
        {
            var lines = new List<string>();
            int failed = 0;
            void Check(string name, bool ok, Sim s)
            {
                lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}: {s}");
                if (!ok) failed++;
            }

            var s = new Sim().Go().Wait(0.5f);
            Check("未选中按 W：只选中，不改档，仍停驻仰姿", s.gears.Selected && s.gears.Gear == Gear.Park && s.Supine && s.events.Count == 1 && s.events[0].kind == ControlEventKind.Select, s);

            s = new Sim().Run(0.6f, go: true).Run(Frame);
            Check("未选中按住 W：只选中，不冲刺", s.gears.Selected && s.gears.Gear == Gear.Park && !s.gears.Accelerating, s);

            s = new Sim().Guide(0.3f, Head + Vector3.left).Back().Wait(0.5f);
            Check("未选中：鼠标不引导，S 也不改变移动方式", !s.gears.Selected && s.gears.Gear == Gear.Park && s.Supine && Vector3.Angle(s.gears.Heading, Vector3.forward) < 1f && s.events.Count == 0, s);

            s = new Sim { point = Head + Vector3.right }.Click().Wait(0.5f);
            Check("未选中单击水面：转向点击处，蹬一次轻的，仍停驻仰姿", !s.gears.Selected && s.Kicks == 1 && s.events[0].strength == s.settings.parkKick && Vector3.Angle(s.events[0].direction, Vector3.right) < 1f && s.gears.Gear == Gear.Park && s.Supine, s);

            s = new Sim().Go().Go();
            bool slow = s.gears.Gear == Gear.Slow && s.Supine && s.gears.Gait == Gait.SupineAlternate;
            s.Go();
            Check("选中后每按一下 W 升一档：停驻 → 慢游（仰姿交替）→ 巡航（俯身齐蹬）", slow && s.gears.Gear == Gear.Cruise && s.gears.Posture == Posture.Prone && s.gears.Gait == Gait.ProneTogether && s.Kicks == 0, s);

            s = new Sim(Gear.Cruise).Go().Wait(0.5f);
            Check("巡航再点 W：不升档，不蹬腿", s.gears.Gear == Gear.Cruise && s.events.Count == 0, s);

            s = new Sim(Gear.Slow) { speedBL = 0.8f }.HoldGo(0.6f);
            Check("慢游按住 W：只升到巡航，不冲刺", s.gears.Gear == Gear.Cruise && !s.gears.Accelerating, s);

            s = new Sim(Gear.Cruise) { speedBL = 1.2f }.Run(0.5f, go: true);
            bool kicking = s.gears.Gear == Gear.Sprint && s.gears.Accelerating && !s.gears.Dolphin && s.gears.Gait == Gait.ProneTogether && s.gears.SpeedBL == s.settings.sprintSpeedBL;
            s.speedBL = 1.85f;
            s.Run(0.1f, go: true);
            Check("巡航按住 W：加速，先俯身蹬腿，到冲刺速度后转海豚", kicking && s.gears.Dolphin && s.gears.Gait == Gait.Dolphin, s);

            s = new Sim(Gear.Cruise) { speedBL = 1.2f }.Run(0.5f, go: true).Run(1.5f, go: true);
            Check("按住一直到不了冲刺速度：dolphinAfter 秒后也转海豚", s.gears.Dolphin, s);

            s = new Sim(Gear.Cruise) { speedBL = 1.2f }.HoldGo(0.6f).Wait(0.5f);
            Check("没到海豚就松开 W：继续俯身巡航", s.gears.Gear == Gear.Cruise && s.Kicks == 0, s);

            s = InWave().Run(Frame);
            Check("海豚段松开 W：停止加速，仍在海豚段", s.gears.Dolphin && !s.gears.Accelerating && s.gears.SpeedBL == s.settings.cruiseSpeedBL, s);

            s = InWave().Run(Frame).Go().Wait(0.5f);
            Check("海豚段点 W：起跳一次，不蹬腿", s.Jumps == 1 && s.Kicks == 0, s);

            s = InWave().Run(Frame).Run(0.5f, go: true);
            Check("海豚段按 W 不放：起跳一次，接着继续冲刺", s.Jumps == 1 && s.gears.Accelerating && s.gears.Dolphin, s);

            s = InWave().Run(Frame);
            s.speedBL = 1.2f;
            s.Wait(0.05f).Go().Wait(0.5f);
            Check("松开后已减速，但在 jumpGrace 内：仍可起跳", s.Jumps == 1, s);

            s = InWave().Run(Frame);
            s.speedBL = 1.2f;
            s.Wait(1f);
            bool cruising = s.gears.Gear == Gear.Cruise && s.gears.Posture == Posture.Prone;
            s.Go().Wait(0.5f);
            Check("宽限过后慢下来：回到巡航，点 W 不起跳", cruising && s.Jumps == 0, s);

            s = InWave().Run(Frame);
            s.speedBL = 1.9f;
            s.Wait(1.5f).Go().Wait(0.5f);
            Check("松开很久但还在冲刺速度：仍可起跳", s.Jumps == 1, s);

            s = InWave().Run(Frame).Back();
            Check("冲刺中点 S：降回巡航，不再在海豚段", s.gears.Gear == Gear.Cruise && !s.gears.Dolphin && s.gears.Selected, s);

            s = new Sim(Gear.Cruise).Back();
            bool down = s.gears.Gear == Gear.Slow && s.Supine && s.gears.Selected;
            s.Back();
            bool parked = s.gears.Gear == Gear.Park && s.gears.Selected;
            s.Back();
            Check("S 点按：每下降一档，退出巡航翻回仰姿；停驻再点解除选中", down && parked && !s.gears.Selected && s.gears.Gear == Gear.Park && s.Supine, s);

            s = new Sim(Gear.Cruise).Run(0.5f, back: true);
            bool braking = s.gears.Braking && s.gears.Gear == Gear.Park && !s.gears.Selected && s.gears.SpeedBL == 0f;
            s.Run(Frame);
            Check("S 按住：急停到停驻并解除选中，回到仰姿；松开结束刹水", braking && !s.gears.Braking && s.Supine, s);

            s = new Sim(Gear.Cruise).Run(0.5f, back: true).Run(Frame).Go().Wait(0.3f);
            Check("急停解除后再按 W：只重新选中，不直接回到巡航", s.gears.Selected && s.gears.Gear == Gear.Park, s);

            s = new Sim(Gear.Slow);
            s.Guide(0.5f * s.settings.headLead, Head + Vector3.left);
            bool headFirst = s.gears.Selected && Vector3.Angle(s.gears.Heading, Vector3.left) < 1f && Vector3.Angle(s.gears.BodyHeading, Vector3.forward) < 1f && s.gears.AimBlend == 1f;
            s.Wait(s.settings.headLead);
            Check("选中后光标即引导点：头先朝它，身体晚 headLead 跟上，不改档",
                headFirst && Vector3.Angle(s.gears.BodyHeading, Vector3.left) < 1f && s.gears.AimPoint == Head + Vector3.left && s.gears.Gear == Gear.Slow, s);

            s = new Sim().Go().Guide(0.3f, Head + Vector3.left);
            s.Back().Guide(s.settings.aimFade + 0.05f, Head + Vector3.right);
            Check("停驻点 S 解除选中：瞄准淡出，光标不再引导", !s.gears.Selected && s.gears.AimBlend == 0f && Vector3.Angle(s.gears.Heading, Vector3.left) < 1f, s);

            s = new Sim().Go().Guide(0.2f, Head + Vector3.left);
            Check("选中停驻时引导大角度转弯：带一点漂移", s.gears.SpeedBL == s.settings.parkDriftBL, s);

            s = new Sim(Gear.Slow) { point = Head + Vector3.right }.Click();
            Check("选中后单击水面：转向并蹬一次，不改档", s.Kicks == 1 && s.events[0].strength == s.settings.swimKick && s.gears.Gear == Gear.Slow && Vector3.Angle(s.events[0].direction, Vector3.right) < 1f, s);

            s = new Sim().Run(0.1f, click: true, onOtter: true).Run(Frame);
            Check("单击海獭（光圈内）：沿当前朝向蹬一次，不转向", s.Kicks == 1 && Vector3.Angle(s.events[0].direction, Vector3.forward) < 1f && Vector3.Angle(s.gears.Heading, Vector3.forward) < 1f, s);

            s = InWave().Run(Frame).Click();
            Check("冲刺中单击：只转向，不蹬腿", s.Kicks == 0, s);

            s = InWave().Run(Frame);
            s.airborne = true;
            s.Go().Click().Wait(0.5f);
            Check("空中的按键和点击作废", s.Jumps == 0 && s.Kicks == 0, s);

            lines.Insert(0, $"# controls self-test {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}: {lines.Count - failed}/{lines.Count} pass");
            Directory.CreateDirectory("Logs");
            File.WriteAllLines(Path.Combine("Logs", "Controls_SelfTest.txt"), lines);
            Debug.Log($"[ControlsSelfTest] {lines.Count - 1 - failed}/{lines.Count - 1} pass, see Logs/Controls_SelfTest.txt");
        }
    }
}
