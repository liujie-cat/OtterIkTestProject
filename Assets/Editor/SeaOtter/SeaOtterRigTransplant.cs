using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OtterIK.EditorTools
{
    /// <summary>
    /// Moves the scene's IK rig from the cat model instance onto the generated sea otter model.
    /// The otter skeleton keeps the cat's bone names, hierarchy and bone-local axes
    /// (Tools/Blender/rig_sea_otter.py), so bones are matched by hierarchy path.
    /// Everything is one Undo step; the scene is not saved.
    /// </summary>
    public class SeaOtterRigTransplant : EditorWindow
    {
        const string OtterModelPath = "Assets/ArtResource/Otter/SeaOtter/sea_otter.fbx";
        const string HipBone = "root.x";
        const string ChestBone = "spine_03.x";

        // Objects farther than this (in chain lengths) from every IK joint keep their local pose.
        const float FitRadiusInChainLengths = 1.5f;

        static readonly HashSet<string> SkipProps = new HashSet<string>
        {
            "m_Script", "m_GameObject", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset"
        };

        GameObject catInstance;
        GameObject otterModel;
        bool alignSpineCenter = true;
        bool fitLimbTargets = true;
        Vector2 scroll;
        string lastLog = "";

        [MenuItem("Tools/Sea Otter/Rig Transplant...")]
        static void Open() => GetWindow<SeaOtterRigTransplant>("Sea Otter Rig");

        [MenuItem("Tools/Sea Otter/Report Cat Rig")]
        static void ReportCatMenu()
        {
            var cat = FindCatInstance();
            if (cat == null) { Debug.LogWarning("[SeaOtterRig] No cat model instance found in the open scene."); return; }
            WriteLog("SeaOtterRig_Report.txt", Report(cat));
        }

        void OnEnable()
        {
            if (catInstance == null) catInstance = FindCatInstance();
            if (otterModel == null) otterModel = AssetDatabase.LoadAssetAtPath<GameObject>(OtterModelPath);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Instantiates the sea otter next to the cat, copies the components added to cat bones onto the same-named " +
                "otter bones, moves the added IK targets/poles/rest points, remaps every scene reference from the cat " +
                "hierarchy to the otter, then deactivates the cat. One Undo step; save the scene yourself.",
                MessageType.Info);

            catInstance = (GameObject)EditorGUILayout.ObjectField("Cat instance", catInstance, typeof(GameObject), true);
            otterModel = (GameObject)EditorGUILayout.ObjectField("Otter model", otterModel, typeof(GameObject), false);
            alignSpineCenter = EditorGUILayout.Toggle(
                new GUIContent("Align spine center", "Offset the otter so the hip-chest midpoint sits where the cat's did."),
                alignSpineCenter);
            fitLimbTargets = EditorGUILayout.Toggle(
                new GUIContent("Fit limb targets",
                    "Added objects near an IK chain keep their offset from the nearest joint, scaled by otter/cat chain length."),
                fitLimbTargets);

            using (new EditorGUI.DisabledScope(catInstance == null || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Report cat rig"))
                    lastLog = WriteLog("SeaOtterRig_Report.txt", Report(catInstance));
                using (new EditorGUI.DisabledScope(otterModel == null))
                {
                    if (GUILayout.Button("Transplant to sea otter", GUILayout.Height(32)))
                        lastLog = WriteLog("SeaOtterRig_Transplant.txt", Transplant(catInstance, otterModel, alignSpineCenter, fitLimbTargets));
                }
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.TextArea(lastLog, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ discovery

        static T[] FindAll<T>() where T : UnityEngine.Object =>
            UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        static GameObject FindCatInstance()
        {
            foreach (var t in FindAll<Transform>())
            {
                if (t.name != HipBone) continue;
                var root = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
                if (root == null || !root.activeInHierarchy) continue;
                var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root);
                if (src != null && AssetDatabase.GetAssetPath(src) == OtterModelPath) continue;
                return root;
            }
            return null;
        }

        static Transform FindDeep(Transform root, string name) =>
            root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform OwnerTransform(UnityEngine.Object o) =>
            o is Transform t ? t : o is GameObject g ? g.transform : o is Component c ? c.transform : null;

        static IEnumerable<(SerializedProperty prop, UnityEngine.Object value)> ObjectRefs(SerializedObject so)
        {
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference || SkipProps.Contains(it.name)) continue;
                if (it.objectReferenceValue != null) yield return (it, it.objectReferenceValue);
            }
        }

        // ------------------------------------------------------------------ report

        static string Report(GameObject model)
        {
            var sb = new StringBuilder();
            var root = model.transform;
            sb.AppendLine($"== Rig report for '{PathOf(root)}' ({AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(model))})");

            sb.AppendLine("\n-- GameObjects added under bones (scene-owned):");
            foreach (var a in PrefabUtility.GetAddedGameObjects(model))
            {
                var t = a.instanceGameObject.transform;
                sb.AppendLine($"  {t.name,-28} parent={t.parent.name,-24} local={t.localPosition:F4} world={t.position:F4}");
            }

            sb.AppendLine("\n-- Components added to bones:");
            foreach (var a in PrefabUtility.GetAddedComponents(model))
                sb.AppendLine($"  {a.instanceComponent.GetType().Name,-30} on {a.instanceComponent.name}");

            sb.AppendLine("\n-- Bone transforms overridden in the scene:");
            foreach (var o in PrefabUtility.GetObjectOverrides(model))
            {
                if (!(o.instanceObject is Transform t)) continue;
                var src = PrefabUtility.GetCorrespondingObjectFromSource(t);
                float dRot = src != null ? Quaternion.Angle(src.localRotation, t.localRotation) : 0f;
                float dPos = src != null ? Vector3.Distance(src.localPosition, t.localPosition) : 0f;
                if (dRot > 0.01f || dPos > 1e-5f)
                    sb.AppendLine($"  {t.name,-28} rotDelta={dRot,6:F2}deg posDelta={dPos:F4}");
            }

            sb.AppendLine("\n-- Scene references into this model:");
            foreach (var mb in FindAll<MonoBehaviour>())
            {
                if (mb == null) continue;
                foreach (var (prop, value) in ObjectRefs(new SerializedObject(mb)))
                {
                    var ot = OwnerTransform(value);
                    if (ot == null || !ot.IsChildOf(root)) continue;
                    sb.AppendLine($"  {mb.name}/{mb.GetType().Name}.{prop.propertyPath} -> {ot.name} ({value.GetType().Name})");
                }
            }

            sb.AppendLine("\n-- Two-bone IK chains:");
            foreach (var ik in root.GetComponentsInChildren<AdvancedTwoBoneIK>(true))
            {
                if (ik.root?.bone == null || ik.mid?.bone == null || ik.end?.bone == null) continue;
                float l1 = Vector3.Distance(ik.root.bone.position, ik.mid.bone.position);
                float l2 = Vector3.Distance(ik.mid.bone.position, ik.end.bone.position);
                string tgt = ik.target ? $"{ik.target.name} d={Vector3.Distance(ik.root.bone.position, ik.target.position):F4}" : "none";
                sb.AppendLine($"  {ik.name}: {ik.root.bone.name} -> {ik.mid.bone.name} -> {ik.end.bone.name}  len={l1:F4}+{l2:F4}  target={tgt}  pole={(ik.pole ? ik.pole.name : "none")}");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ transplant

        struct Chain
        {
            public AdvancedTwoBoneIK ik;
            public Transform[] cat;
            public float catLength;
        }

        static string Transplant(GameObject cat, GameObject otterAsset, bool alignSpine, bool fitTargets)
        {
            var log = new StringBuilder();
            var catRoot = cat.transform;

            if (otterAsset.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
            {
                string why = $"'{AssetDatabase.GetAssetPath(otterAsset)}' has no SkinnedMeshRenderer, so the IK would move bones " +
                             "but not the mesh. Set its import Rig > Animation Type to Generic and try again. Nothing was changed.";
                Debug.LogError("[SeaOtterRig] " + why);
                return why;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Transplant cat rig to sea otter");

            // 1. otter instance with the cat's local pose
            var otter = (GameObject)PrefabUtility.InstantiatePrefab(otterAsset, catRoot.parent);
            Undo.RegisterCreatedObjectUndo(otter, "Create sea otter");
            var otterRoot = otter.transform;
            otterRoot.SetSiblingIndex(catRoot.GetSiblingIndex() + 1);
            otterRoot.localPosition = catRoot.localPosition;
            otterRoot.localRotation = catRoot.localRotation;
            otterRoot.localScale = catRoot.localScale;
            log.AppendLine($"Created '{PathOf(otterRoot)}' from {AssetDatabase.GetAssetPath(otterAsset)}");

            // 2. bone map by hierarchy path (scene-added objects excluded)
            var addedRoots = PrefabUtility.GetAddedGameObjects(cat)
                .Select(a => a.instanceGameObject.transform).Where(t => t != null).ToList();
            bool IsAdded(Transform t) => addedRoots.Any(a => t == a || t.IsChildOf(a));

            var map = new Dictionary<Transform, Transform>();
            var otterSmr = otter.GetComponentInChildren<SkinnedMeshRenderer>(true);
            foreach (var t in catRoot.GetComponentsInChildren<Transform>(true))
            {
                if (IsAdded(t)) continue;
                string path = AnimationUtility.CalculateTransformPath(t, catRoot);
                var o = path.Length == 0 ? otterRoot : otterRoot.Find(path);
                if (o == null && t.GetComponent<SkinnedMeshRenderer>() != null && otterSmr != null) o = otterSmr.transform;
                if (o != null) map[t] = o;
            }
            var approximated = new HashSet<Transform>();
            Transform MapT(Transform t)
            {
                for (var p = t; p != null; p = p.parent)
                {
                    if (!map.TryGetValue(p, out var o)) continue;
                    if (p != t && approximated.Add(t))
                        log.AppendLine($"  ! '{t.name}' has no otter counterpart, using ancestor '{o.name}'");
                    return o;
                }
                return null;
            }
            UnityEngine.Object MapObject(UnityEngine.Object v)
            {
                var t = OwnerTransform(v);
                if (t == null || !t.IsChildOf(catRoot)) return null;
                var mt = MapT(t);
                if (mt == null) return null;
                if (v is Transform) return mt;
                if (v is GameObject) return mt.gameObject;
                var c = (Component)v;
                var src = t.GetComponents(c.GetType());
                var dst = mt.GetComponents(c.GetType());
                int i = Array.IndexOf(src, c);
                return i >= 0 && i < dst.Length ? dst[i] : dst.FirstOrDefault();
            }
            log.AppendLine($"Mapped {map.Count} cat transforms by path.");

            // 3. align body centers, snapshot cat-side data used for fitting
            var catHip = FindDeep(catRoot, HipBone);
            var catChest = FindDeep(catRoot, ChestBone);
            if (alignSpine && catHip && catChest && map.ContainsKey(catHip) && map.ContainsKey(catChest))
            {
                Vector3 offset = (catHip.position + catChest.position) * 0.5f - (map[catHip].position + map[catChest].position) * 0.5f;
                otterRoot.position += offset;
                log.AppendLine($"Aligned hip-chest midpoint: otter offset {offset:F4} (world)");
            }

            var chains = new List<Chain>();
            foreach (var ik in catRoot.GetComponentsInChildren<AdvancedTwoBoneIK>(true))
            {
                if (ik.root?.bone == null || ik.mid?.bone == null || ik.end?.bone == null) continue;
                var j = new[] { ik.root.bone, ik.mid.bone, ik.end.bone };
                chains.Add(new Chain
                {
                    ik = ik,
                    cat = j,
                    catLength = Vector3.Distance(j[0].position, j[1].position) + Vector3.Distance(j[1].position, j[2].position)
                });
            }
            var addedWorld = addedRoots.ToDictionary(t => t, t => (pos: t.position, rot: t.rotation));

            // 4. copy components added to cat bones
            var copies = new List<(Behaviour behaviour, bool enabled)>();
            var addedComponents = PrefabUtility.GetAddedComponents(cat)
                .Select(a => a.instanceComponent)
                .Where(c => c != null && !(c is Transform))
                .OrderBy(c => c.gameObject.GetInstanceID())
                .ThenBy(c => Array.IndexOf(c.GetComponents<Component>(), c))
                .ToList();
            foreach (var c in addedComponents)
            {
                var dst = MapT(c.transform);
                if (dst == null) { log.AppendLine($"  ! no target bone for {c.GetType().Name} on '{c.name}'"); continue; }
                var nc = Undo.AddComponent(dst.gameObject, c.GetType());
                if (nc == null) { log.AppendLine($"  ! could not add {c.GetType().Name} to '{dst.name}'"); continue; }
                Undo.RecordObject(nc, "Copy component");
                EditorUtility.CopySerialized(c, nc);
                if (nc is Behaviour b)
                {
                    copies.Add((b, b.enabled));
                    b.enabled = false;
                }
                log.AppendLine($"Copied {c.GetType().Name,-30} {c.name} -> {dst.name}");
            }

            // 5. move scene-added objects (targets, poles, rest points) onto the otter bones
            foreach (var a in addedRoots)
            {
                var newParent = MapT(a.parent);
                if (newParent == null) { log.AppendLine($"  ! no new parent for '{a.name}'"); continue; }
                Vector3 lp = a.localPosition; Quaternion lr = a.localRotation; Vector3 ls = a.localScale;
                Undo.SetTransformParent(a, newParent, "Move rig object");
                Undo.RecordObject(a, "Move rig object");
                a.localPosition = lp; a.localRotation = lr; a.localScale = ls;
                log.AppendLine($"Moved {a.name,-28} under {newParent.name}");
            }

            // 6. remap every scene reference that points into the cat hierarchy
            int remapped = 0;
            var unmapped = new List<string>();
            foreach (var mb in FindAll<MonoBehaviour>())
            {
                if (mb == null || mb.transform.IsChildOf(catRoot)) continue;
                var so = new SerializedObject(mb);
                bool changed = false;
                foreach (var (prop, value) in ObjectRefs(so))
                {
                    var owner = OwnerTransform(value);
                    if (owner == null || !owner.IsChildOf(catRoot)) continue;
                    var nv = MapObject(value);
                    if (nv == null) { unmapped.Add($"{mb.name}/{mb.GetType().Name}.{prop.propertyPath} -> {owner.name}"); continue; }
                    prop.objectReferenceValue = nv;
                    changed = true;
                    remapped++;
                    log.AppendLine($"  ref {mb.name}/{mb.GetType().Name}.{prop.propertyPath}: {owner.name} -> {OwnerTransform(nv).name}");
                }
                if (changed) so.ApplyModifiedProperties();
            }
            log.AppendLine($"Remapped {remapped} references.");
            foreach (var u in unmapped) log.AppendLine($"  ! unmapped: {u}");

            // 7. fit limb targets to otter proportions
            if (fitTargets)
            {
                foreach (var a in addedRoots)
                {
                    var (catPos, catRot) = addedWorld[a];
                    float best = float.MaxValue; Chain bestChain = default; int bestJoint = -1;
                    foreach (var ch in chains)
                        for (int k = 0; k < 3; k++)
                        {
                            float d = Vector3.Distance(catPos, ch.cat[k].position) / Mathf.Max(ch.catLength, 1e-5f);
                            if (d < best) { best = d; bestChain = ch; bestJoint = k; }
                        }
                    if (bestJoint < 0 || best > FitRadiusInChainLengths)
                    {
                        log.AppendLine($"Kept local pose   {a.name,-28} (not near an IK chain)");
                        continue;
                    }
                    var oj = bestChain.cat.Select(MapT).ToArray();
                    float otterLength = Vector3.Distance(oj[0].position, oj[1].position) + Vector3.Distance(oj[1].position, oj[2].position);
                    float ratio = otterLength / bestChain.catLength;
                    Undo.RecordObject(a, "Fit rig object");
                    a.position = oj[bestJoint].position + (catPos - bestChain.cat[bestJoint].position) * ratio;
                    a.rotation = catRot;
                    log.AppendLine($"Fitted            {a.name,-28} to {oj[bestJoint].name} of {bestChain.ik.name} (length ratio {ratio:F3})");
                }
            }

            foreach (var (b, enabled) in copies)
            {
                Undo.RecordObject(b, "Enable component");
                b.enabled = enabled;
            }

            // 8. retire the cat
            Undo.RecordObject(cat, "Deactivate cat");
            cat.SetActive(false);

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = otter;
            log.AppendLine($"Deactivated '{cat.name}'. Undo reverts the whole transplant.");

            log.AppendLine("\n-- Bone frame check (cat vs otter, degrees between world axes):");
            foreach (var ik in otterRoot.GetComponentsInChildren<AdvancedTwoBoneIK>(true))
            {
                var catIk = chains.FirstOrDefault(c => c.ik.name == ik.name && c.ik.GetType() == ik.GetType()).ik;
                if (catIk == null) continue;
                foreach (var (o, c) in new[] { (ik.root, catIk.root), (ik.mid, catIk.mid), (ik.end, catIk.end) })
                {
                    if (o?.bone == null || c?.bone == null) continue;
                    float up = Vector3.Angle(o.bone.TransformDirection(o.upLocal), c.bone.TransformDirection(c.upLocal));
                    float fwd = Vector3.Angle(o.bone.TransformDirection(o.forwardLocal), c.bone.TransformDirection(c.forwardLocal));
                    log.AppendLine($"  {ik.name,-24} {o.bone.name,-26} up {up,6:F1}  forward {fwd,6:F1}");
                }
            }

            log.AppendLine();
            log.Append(Report(otter));
            return log.ToString();
        }

        static string WriteLog(string file, string text)
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(Path.Combine("Logs", file), text);
            Debug.Log($"[SeaOtterRig] wrote Logs/{file}\n{text}");
            return text;
        }
    }
}
