using System;
using System.Globalization;
using Il2CppInterop.Runtime;
using UnityEngine;

/// <summary>
/// Client readback of the Sample effects root after a tracked Charm borrow.
/// Per-child lines go through Scratch.Log. The return value is a compact summary.
/// </summary>
public static class Snippet
{
    const string SampleRoot = "progenitor-effects-progenitor.sample";

    public static object Run()
    {
        try
        {
            return Readback();
        }
        catch (Exception e)
        {
            string summary = "CHARMROOT error=" + e.GetType().Name;
            try { Scratch.Log(summary); } catch { }
            return summary;
        }
    }

    static object Readback()
    {
        var world = Scratch.Client;
        if (world == null || !world.IsCreated)
            return "CHARMROOT error=no-client-world";

        var type = Il2CppType.Of<GameObject>();
        if (type == null)
            return "CHARMROOT error=no-gameobject-type";

        Il2CppSystem.Object[]? arr = null;
        try { arr = Resources.FindObjectsOfTypeAll(type); }
        catch { return "CHARMROOT error=find-threw"; }
        if (arr == null)
            return "CHARMROOT error=find-null";

        int length = 0;
        try { length = arr.Length; }
        catch { return "CHARMROOT error=find-length"; }

        GameObject? root = null;
        int failures = 0;
        for (int i = 0; i < length; i++)
        {
            try
            {
                var obj = arr[i];
                if (obj == null) continue;
                var go = obj.TryCast<GameObject>();
                // Unity fake-null: Il2CppInterop overloaded == / implicit bool, not C# is-null.
                if (go == null) continue;
                string name;
                try { name = go.name ?? ""; }
                catch { continue; }
                if (name != SampleRoot) continue;
                root = go;
                break;
            }
            catch
            {
                failures++;
            }
        }

        if (root == null)
        {
            Scratch.Log("CHARMROOT absent name=" + SampleRoot);
            return "CHARMROOT absent";
        }

        Transform? transform = null;
        try { transform = root.transform; }
        catch { transform = null; }
        if (transform == null)
        {
            string noxf = "CHARMROOT children=0 playingChildren=0 failures=" + failures.ToString(CultureInfo.InvariantCulture);
            Scratch.Log(noxf);
            return noxf;
        }

        int childCount = 0;
        try { childCount = transform.childCount; }
        catch { childCount = 0; }

        int playingChildren = 0;
        for (int c = 0; c < childCount; c++)
        {
            try
            {
                Transform? child = null;
                try { child = transform.GetChild(c); }
                catch { failures++; continue; }
                if (child == null) continue;

                GameObject? childGo = null;
                try { childGo = child.gameObject; }
                catch { failures++; continue; }
                if (childGo == null) continue;

                bool activeSelf = false;
                string childName = "";
                try
                {
                    activeSelf = childGo.activeSelf;
                    childName = childGo.name ?? "";
                }
                catch { }

                int playing = 0, systems = 0;
                try
                {
                    var ps = childGo.GetComponentsInChildren<ParticleSystem>(true);
                    if (ps != null)
                    {
                        for (int p = 0; p < ps.Length; p++)
                        {
                            var system = ps[p];
                            if (system == null) continue;
                            systems++;
                            bool isPlaying = false;
                            try { isPlaying = system.isPlaying; }
                            catch { }
                            if (isPlaying) playing++;
                        }
                    }
                }
                catch { }

                if (playing > 0) playingChildren++;
                Scratch.Log(
                    "CHARMCHILD name=" + childName
                    + " activeSelf=" + activeSelf
                    + " systems=" + systems
                    + " playing=" + playing);
            }
            catch
            {
                failures++;
            }
        }

        string summary = "CHARMROOT children=" + childCount.ToString(CultureInfo.InvariantCulture)
            + " playingChildren=" + playingChildren.ToString(CultureInfo.InvariantCulture)
            + " failures=" + failures.ToString(CultureInfo.InvariantCulture);
        Scratch.Log(summary);
        return summary;
    }
}
