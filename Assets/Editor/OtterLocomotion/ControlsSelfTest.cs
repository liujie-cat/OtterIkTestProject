using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Plays scripted keys and pointer through ControlGestures and GearMachine against a body held still facing +z,
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
            public bool airborne, underwater, divingOrSurfacing;
            public Vector3 point = new Vector3(0f, 0f, 1f);
            /// <summary>Times it rolled between back and belly, and whether it stood up, since it started.</summary>
            public int Rolls;
            public bool StoodUp;
            Posture lay;

            public Sim(Gear start = Gear.Park)
            {
                gears.Reset(Body, start);
                lay = gears.Posture;
            }

            GearBody Body => new GearBody
            {
                position = Vector3.zero, head = Head, forward = Vector3.forward, speedBL = speedBL, airborne = airborne,
                underwater = underwater, divingOrSurfacing = divingOrSurfacing,
            };

            public Sim Run(float seconds, bool go = false, bool back = false, bool onOtter = false, bool dive = false)
            {
                for (float t = 0f; t < seconds - 1e-5f; t += Frame)
                {
                    gestures.Clear();
                    Vector3 at = onOtter ? Vector3.zero : point;
                    input.Step(gs, time, go, back, dive, gestures);
                    gears.Step(settings, gestures, time, at, onOtter, Body, events);
                    if (gears.Posture != lay) Rolls++;
                    lay = gears.Posture;
                    StoodUp |= gears.Upright;
                    time += Frame;
                }
                return this;
            }

            public Sim Wait(float seconds) => Run(seconds);
            public Sim LetGo() => Run(gs.releaseTime + Frame);
            public Sim Go() => Run(0.1f, go: true).LetGo();
            public Sim HoldGo(float seconds) => Run(seconds, go: true).LetGo();
            public Sim Back() => Run(0.1f, back: true).LetGo();
            public Sim Dive() => Run(0.1f, dive: true).LetGo();
            public Sim HoldDive(float seconds) => Run(seconds, dive: true);

            // the body's way through the depth, as DivePath would report it
            public Sim Under(bool under = true, bool moving = false)
            {
                underwater = under;
                divingOrSurfacing = moving;
                return Run(Frame);
            }

            public Sim Guide(float seconds, Vector3 to)
            {
                point = to;
                return Run(seconds);
            }

            public int Kicks => events.Count(e => e.kind == ControlEventKind.Kick);
            public int Jumps => events.Count(e => e.kind == ControlEventKind.Jump);
            public int Dives => events.Count(e => e.kind == ControlEventKind.Dive);
            public int Surfacings => events.Count(e => e.kind == ControlEventKind.Surface);
            public bool Supine => gears.Posture == Posture.Supine;
            public override string ToString() =>
                $"{(gears.Selected ? "selected" : "not selected")}, gear {gears.Gear}{(gears.Dolphin ? " wave" : "")}{(gears.Upright ? " upright" : "")}, {gears.Posture}, {gears.Gait}, {gears.SpeedBL:F2} BL/s" +
                $"{(gears.Diving ? ", diving" : "")}{(gears.Descending ? " deeper" : "")}{(underwater ? ", under" : "")}; events [{string.Join(", ", events)}]";
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

            var s = new Sim().Go();
            bool waiting = s.gears.Selected && !s.gears.Upright && s.Supine;
            s.Wait(0.5f);
            Check("只按一下 W：先只选中、仍仰着，standDelay 后站起来踩水（俯身那面朝前），只翻一次", waiting && s.gears.Gear == Gear.Park && s.gears.Upright && s.gears.Posture == Posture.Prone && s.Rolls == 1 && s.events.Count == 1 && s.events[0].kind == ControlEventKind.Select, s);

            s = new Sim().Go().Guide(0.05f, Head + Vector3.left);
            bool headOnly = !s.gears.Upright && Vector3.Angle(s.gears.Heading, Vector3.left) < 1f && Vector3.Angle(s.gears.BodyHeading, Vector3.forward) < 1f && s.gears.SpeedBL == 0f;
            s.Wait(0.3f);
            Check("等着站起来时只有头跟光标转，站起来后身体才跟上", headOnly && s.gears.Upright && Vector3.Angle(s.gears.BodyHeading, Vector3.left) < 1f, s);

            s = new Sim().Go().Go().Wait(0.5f);
            Check("快速按两下 W：选中加慢游，全程仰着，不站", s.gears.Gear == Gear.Slow && s.Supine && !s.StoodUp && s.Rolls == 0, s);

            s = new Sim().Go().Go().Go().Wait(0.5f);
            Check("快速按三下 W：选中、慢游、巡航，中间一直仰着，最后只翻一次成俯身", s.gears.Gear == Gear.Cruise && s.gears.Posture == Posture.Prone && !s.StoodUp && s.Rolls == 1, s);

            s = new Sim().Go().Guide(0.5f, Head + Vector3.right);
            Check("绿圈站立：光标引导转向，不蹬腿，仍站着", s.Kicks == 0 && s.gears.Upright && Vector3.Angle(s.gears.Heading, Vector3.right) < 1f, s);

            s = new Sim().Go().Wait(0.5f).Go();
            bool lying = !s.gears.Upright && s.Supine;
            s.Back();
            waiting = !s.gears.Upright && s.Supine;
            s.Wait(0.5f);
            Check("站立按 W 躺回仰泳；慢游按 S 回停驻，仍选中，仰着等一下再重新站起来", lying && waiting && s.gears.Gear == Gear.Park && s.gears.Selected && s.gears.Upright, s);

            s = new Sim(Gear.Slow).Back().Back().Wait(0.5f);
            Check("慢游快速点两下 S：直接解除选中，仰着不站", !s.gears.Selected && s.Supine && !s.StoodUp && s.Rolls == 0, s);

            s = new Sim().Go().Wait(0.5f).Dive();
            bool diving = s.gears.Diving && !s.gears.Upright && s.gears.Posture == Posture.Prone;
            s.Under().Dive().Under(false);
            int rolls = s.Rolls;
            waiting = !s.gears.Upright && s.gears.Posture == Posture.Prone;
            s.Wait(0.5f);
            Check("站立按空格：下潜时不再站着；上浮回到水面俯身等一下，重新站起来，不先翻回仰姿", diving && waiting && !s.gears.Diving && s.gears.Upright && s.Rolls == rolls, s);

            s = new Sim().Go().Wait(0.5f).Back();
            Check("站立点 S 解除选中：躺回仰姿", !s.gears.Selected && !s.gears.Upright && s.Supine, s);

            s = new Sim().Run(0.6f, go: true).LetGo();
            Check("未选中按住 W：只选中，不冲刺", s.gears.Selected && s.gears.Gear == Gear.Park && !s.gears.Accelerating, s);

            s = new Sim().Guide(0.3f, Head + Vector3.left).Back().Wait(0.5f);
            Check("未选中：鼠标不引导，S 也不改变移动方式", !s.gears.Selected && s.gears.Gear == Gear.Park && s.Supine && Vector3.Angle(s.gears.Heading, Vector3.forward) < 1f && s.events.Count == 0, s);

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

            s = InWave().LetGo();
            Check("海豚段松开 W：停止加速，仍在海豚段", s.gears.Dolphin && !s.gears.Accelerating && s.gears.SpeedBL == s.settings.cruiseSpeedBL, s);

            s = InWave().LetGo().Go().Wait(0.5f);
            Check("海豚段点 W：起跳一次，不蹬腿", s.Jumps == 1 && s.Kicks == 0, s);

            s = InWave().LetGo().Run(0.5f, go: true);
            Check("海豚段按 W 不放：起跳一次，接着继续冲刺", s.Jumps == 1 && s.gears.Accelerating && s.gears.Dolphin, s);

            s = InWave().LetGo();
            s.speedBL = 1.2f;
            s.Wait(0.05f).Go().Wait(0.5f);
            Check("松开后已减速，但在 jumpGrace 内：仍可起跳", s.Jumps == 1, s);

            s = InWave().LetGo();
            s.speedBL = 1.2f;
            s.Wait(1f);
            bool cruising = s.gears.Gear == Gear.Cruise && s.gears.Posture == Posture.Prone;
            s.Go().Wait(0.5f);
            Check("宽限过后慢下来：回到巡航，点 W 不起跳", cruising && s.Jumps == 0, s);

            s = InWave().LetGo();
            s.speedBL = 1.9f;
            s.Wait(1.5f).Go().Wait(0.5f);
            Check("松开很久但还在冲刺速度：仍可起跳", s.Jumps == 1, s);

            s = InWave().LetGo().Back();
            Check("冲刺中点 S：降回巡航，不再在海豚段", s.gears.Gear == Gear.Cruise && !s.gears.Dolphin && s.gears.Selected, s);

            s = new Sim(Gear.Cruise).Back();
            bool down = s.gears.Gear == Gear.Slow && s.Supine && s.gears.Selected;
            s.Back();
            bool parked = s.gears.Gear == Gear.Park && s.gears.Selected;
            s.Back();
            Check("S 点按：每下降一档，退出巡航翻回仰姿；停驻再点解除选中", down && parked && !s.gears.Selected && s.gears.Gear == Gear.Park && s.Supine, s);

            s = new Sim(Gear.Cruise).Run(0.05f, back: true).Run(Frame).Run(0.05f, back: true).LetGo();
            Check("按着 S 闪断一帧：仍算一下，只降一档到仰泳", s.gears.Gear == Gear.Slow && s.gears.Selected && s.events.Count == 1, s);

            s = new Sim(Gear.Cruise).Run(0.5f, back: true);
            bool braking = s.gears.Braking && s.gears.Gear == Gear.Park && !s.gears.Selected && s.gears.SpeedBL == 0f;
            s.LetGo();
            Check("S 按住：急停到停驻并解除选中，回到仰姿；松开结束刹水", braking && !s.gears.Braking && s.Supine, s);

            s = new Sim(Gear.Cruise).Run(0.5f, back: true).LetGo().Go().Wait(0.3f);
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

            s = new Sim(Gear.Slow).Run(0.3f, onOtter: true);
            Check("选中后光标在海獭上（光圈内）：不引导，朝向不变", Vector3.Angle(s.gears.Heading, Vector3.forward) < 1f && s.events.Count == 0, s);

            s = InWave().LetGo();
            s.airborne = true;
            s.Go().Wait(0.5f);
            Check("空中的按键作废", s.Jumps == 0, s);

            s = new Sim(Gear.Cruise) { speedBL = 1.2f }.Dive().Wait(0.5f);
            Check("巡航按空格：扎猛子下潜一次，松开不上浮", s.gears.Diving && s.Dives == 1 && s.Surfacings == 0 && s.gears.Posture == Posture.Prone && s.gears.Gear == Gear.Cruise, s);

            s = new Sim(Gear.Slow).Dive();
            bool rolling = s.gears.Diving && s.gears.Posture == Posture.Prone && s.gears.Gait == Gait.ProneTogether && s.gears.Gear == Gear.Slow;
            s.Under();
            Check("仰泳按空格：先翻成俯身，翻好入水即自己换巡航（黄圈），不用再按前进", rolling && s.gears.Gear == Gear.Cruise && s.gears.Gait == Gait.ProneTogether, s);

            s = new Sim(Gear.Cruise).Dive().Under().Back();
            parked = s.gears.Gear == Gear.Park;
            s.Go();
            Check("水下没有慢游：后退从巡航直接停驻，前进从停驻直接巡航", parked && s.gears.Gear == Gear.Cruise, s);

            s = new Sim().Dive().Under(moving: true);
            Check("停驻按空格：下潜中至少以 diveSpeedBL 游下去", s.gears.Diving && s.gears.SpeedBL == s.settings.diveSpeedBL, s);

            s = new Sim(Gear.Cruise).Run(0.6f, dive: true);
            bool deeper = s.gears.Diving && s.gears.Descending;
            s.LetGo();
            Check("按住空格下潜：按满 holdTime 起继续往下，松开保持深度、不上浮", deeper && s.gears.Diving && !s.gears.Descending && s.Surfacings == 0, s);

            s = new Sim(Gear.Cruise).Dive().Under().HoldDive(0.6f);
            deeper = s.gears.Descending;
            s.LetGo();
            Check("水下按住空格：继续下潜，松开保持当前深度", deeper && !s.gears.Descending && s.gears.Diving && s.Surfacings == 0, s);

            s = new Sim(Gear.Slow).Dive().Under().Dive();
            bool rising = s.Surfacings == 1 && !s.gears.Diving && s.gears.Posture == Posture.Prone;
            s.Under(false);
            Check("水下点空格：上浮；出水后仍是巡航俯身", rising && !s.Supine && s.gears.Gear == Gear.Cruise, s);

            s = new Sim(Gear.Cruise).Dive().Under().Dive().Under(moving: true).Dive();
            Check("上浮途中再点空格：重新下潜", s.gears.Diving && s.Dives == 2 && s.Surfacings == 1, s);

            s = InWave().LetGo().Dive().Under().Go().Wait(0.5f);
            Check("水下海豚段点 W：不起跳", s.Jumps == 0 && s.gears.Dolphin && s.gears.Gait == Gait.Dolphin, s);

            s = InWave().LetGo();
            s.airborne = true;
            s.Dive().Wait(0.3f);
            Check("空中按空格作废", s.Dives == 0 && !s.gears.Diving, s);

            lines.Insert(0, $"# controls self-test {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}: {lines.Count - failed}/{lines.Count} pass");
            Directory.CreateDirectory("Logs");
            File.WriteAllLines(Path.Combine("Logs", "Controls_SelfTest.txt"), lines);
            Debug.Log($"[ControlsSelfTest] {lines.Count - 1 - failed}/{lines.Count - 1} pass, see Logs/Controls_SelfTest.txt");
        }
    }
}
