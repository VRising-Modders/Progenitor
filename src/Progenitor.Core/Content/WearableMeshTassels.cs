using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal static partial class WearableMeshBuild
{
    private sealed partial class Acc
    {
        private readonly HashSet<(int A, int B, int C)> _skirtTriangles = new();
        internal int ClothIndexCount => _cloth.Count;

        internal void MarkSkirt(int start)
        {
            for (int i = start; i < _cloth.Count; i += 3)
                _skirtTriangles.Add((_cloth[i], _cloth[i + 1], _cloth[i + 2]));
        }

        internal bool InlayTassels(WearableVec3 origin, WearableVec3 forward,
            float span, int count, float length, WearableSurface surface)
        {
            if (_skirtTriangles.Count == 0 || length <= 0f) return false;
            var right = Right(forward);
            var up = new WearableVec3(0f, 1f, 0f);
            var rebuilt = new List<int>();
            var recolored = new List<int>();
            var replacementSkirt = new HashSet<(int A, int B, int C)>();
            var intersections = new Dictionary<(int A, int B, int Plane), int>();
            int firstCutVertex = _verts.Count;
            count = Math.Max(1, count);
            // Eight millimeters at the belt, tapering to four at the ribbon's end.
            float taper = .002f / length;
            var planes = new (WearableVec3 Normal, float Offset)[count][];
            for (int ribbon = 0; ribbon < count; ribbon++)
            {
                float u = count == 1 ? 0f : 2f * ribbon / (count - 1) - 1f;
                float x = u * span * .40f;
                planes[ribbon] = new[]
                {
                    (up, 0f), (up * -1f, length), (forward * -1f, 0f),
                    (right - up * taper, x + .004f),
                    (right * -1f - up * taper, -x + .004f),
                };
            }
            for (int triangle = 0; triangle < _cloth.Count; triangle += 3)
            {
                int a = _cloth[triangle], b = _cloth[triangle + 1], c = _cloth[triangle + 2];
                if (!_skirtTriangles.Contains((a, b, c)))
                {
                    rebuilt.Add(a); rebuilt.Add(b); rebuilt.Add(c);
                    continue;
                }
                float minY = MathF.Min(_verts[a].Y, MathF.Min(_verts[b].Y, _verts[c].Y));
                float maxY = MathF.Max(_verts[a].Y, MathF.Max(_verts[b].Y, _verts[c].Y));
                float frontA = InlayDot(_verts[a] - origin, forward);
                float frontB = InlayDot(_verts[b] - origin, forward);
                float frontC = InlayDot(_verts[c] - origin, forward);
                if (minY > origin.Y || maxY < origin.Y - length || MathF.Max(frontA, MathF.Max(frontB, frontC)) <= 0f)
                {
                    rebuilt.Add(a); rebuilt.Add(b); rebuilt.Add(c);
                    replacementSkirt.Add((a, b, c));
                    continue;
                }
                var pending = new List<List<int>> { new() { a, b, c } };
                for (int ribbon = 0; ribbon < count && pending.Count > 0; ribbon++)
                {
                    var remaining = new List<List<int>>();
                    foreach (var polygon in pending)
                    {
                        var inside = polygon;
                        for (int plane = 0; plane < planes[ribbon].Length && inside.Count >= 3; plane++)
                        {
                            var p = planes[ribbon][plane];
                            SplitInlay(inside, origin, p.Normal, p.Offset, ribbon * 5 + plane,
                                intersections, out var next, out var outside);
                            if (outside.Count >= 3) remaining.Add(outside);
                            inside = next;
                        }
                        EmitInlay(inside, recolored, null);
                    }
                    pending = remaining;
                }
                foreach (var polygon in pending) EmitInlay(polygon, rebuilt, replacementSkirt);
            }
            // A later plane can add a point to an edge of an already emitted outside
            // polygon. Retessellate BOTH sides of every such edge before skin transfer:
            // merely coincident rest geometry would crack when transfer weights vary.
            var cutSet = new HashSet<int>();
            foreach (int vertex in rebuilt) if (vertex >= firstCutVertex) cutSet.Add(vertex);
            foreach (int vertex in recolored) if (vertex >= firstCutVertex) cutSet.Add(vertex);
            var cuts = new List<int>(cutSet);
            cuts.Sort();
            ConformInlay(rebuilt, cuts, replacementSkirt);
            ConformInlay(recolored, cuts, null);
            _cloth.Clear();
            _cloth.AddRange(rebuilt);
            _skirtTriangles.Clear();
            foreach (var triangle in replacementSkirt) _skirtTriangles.Add(triangle);
            for (int i = 0; i < recolored.Count; i += 3)
                Tri(surface, recolored[i], recolored[i + 1], recolored[i + 2]);
            return true;
        }

        private void ConformInlay(List<int> triangles, IReadOnlyList<int> cuts,
            HashSet<(int A, int B, int C)>? skirt)
        {
            const float tolerance = .0000005f;
            var output = new List<int>();
            var newSkirt = skirt == null ? null : new HashSet<(int A, int B, int C)>();
            for (int triangle = 0; triangle < triangles.Count; triangle += 3)
            {
                int a = triangles[triangle], b = triangles[triangle + 1], c = triangles[triangle + 2];
                bool isSkirt = skirt?.Contains((a, b, c)) == true;
                var boundary = new List<int>();
                int[] corners = { a, b, c };
                for (int edge = 0; edge < 3; edge++)
                {
                    int start = corners[edge], end = corners[(edge + 1) % 3];
                    boundary.Add(start);
                    var delta = _verts[end] - _verts[start];
                    float lengthSquared = InlayDot(delta, delta);
                    if (lengthSquared <= tolerance * tolerance) continue;
                    float limit = tolerance / MathF.Sqrt(lengthSquared);
                    var along = new List<(float T, int Vertex)>();
                    foreach (int cut in cuts)
                    {
                        var fromStart = _verts[cut] - _verts[start];
                        float t = InlayDot(fromStart, delta) / lengthSquared;
                        if (t <= limit || t >= 1f - limit) continue;
                        var error = fromStart - delta * t;
                        if (InlayDot(error, error) <= tolerance * tolerance) along.Add((t, cut));
                    }
                    along.Sort((x, y) => x.T != y.T ? x.T.CompareTo(y.T) : x.Vertex.CompareTo(y.Vertex));
                    float previous = -1f;
                    foreach (var cut in along)
                    {
                        if (cut.T - previous <= limit) continue;
                        boundary.Add(cut.Vertex);
                        previous = cut.T;
                    }
                }
                if (boundary.Count == 3)
                {
                    output.Add(a); output.Add(b); output.Add(c);
                    if (isSkirt) newSkirt!.Add((a, b, c));
                    continue;
                }
                // An interior fan keeps every inserted boundary segment as an edge.
                // A corner fan would make collinear boundary vertices into T-junctions again.
                int midpoint = InterpolateInlay(a, b, .5f);
                int center = InterpolateInlay(midpoint, c, 1f / 3f);
                for (int i = 0; i < boundary.Count; i++)
                {
                    int start = boundary[i], end = boundary[(i + 1) % boundary.Count];
                    output.Add(center); output.Add(start); output.Add(end);
                    if (isSkirt) newSkirt!.Add((center, start, end));
                }
            }
            triangles.Clear(); triangles.AddRange(output);
            if (skirt != null)
            {
                skirt.Clear();
                foreach (var triangle in newSkirt!) skirt.Add(triangle);
            }
        }

        private void SplitInlay(List<int> polygon, WearableVec3 origin, WearableVec3 normal,
            float offset, int plane, Dictionary<(int A, int B, int Plane), int> cache,
            out List<int> inside, out List<int> outside)
        {
            inside = new List<int>(); outside = new List<int>();
            for (int i = 0; i < polygon.Count; i++)
            {
                int a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                float da = InlayDot(_verts[a] - origin, normal) - offset;
                float db = InlayDot(_verts[b] - origin, normal) - offset;
                bool ia = da <= 0f, ib = db <= 0f;
                AddInlayVertex(ia ? inside : outside, a);
                if (ia == ib) continue;
                var key = (Math.Min(a, b), Math.Max(a, b), plane);
                if (!cache.TryGetValue(key, out int intersection))
                {
                    float t = Math.Clamp(da / (da - db), 0f, 1f);
                    intersection = t == 0f ? a : t == 1f ? b : InterpolateInlay(a, b, t);
                    cache[key] = intersection;
                }
                AddInlayVertex(inside, intersection);
                AddInlayVertex(outside, intersection);
            }
            CloseInlay(inside); CloseInlay(outside);
        }

        private int InterpolateInlay(int a, int b, float t)
        {
            var weights = new Dictionary<int, float>();
            void Add(int bone, float weight)
            {
                if (weight <= 0f) return;
                weights.TryGetValue(bone, out float previous);
                weights[bone] = previous + weight;
            }
            Add(_bones[a], (1f - _weights2[a]) * (1f - t));
            Add(_bones2[a], _weights2[a] * (1f - t));
            Add(_bones[b], (1f - _weights2[b]) * t);
            Add(_bones2[b], _weights2[b] * t);
            int first = 0, second = 0;
            float firstWeight = 0f, secondWeight = 0f;
            foreach (var pair in weights)
            {
                if (pair.Value > firstWeight || pair.Value == firstWeight && pair.Key < first)
                {
                    second = first; secondWeight = firstWeight;
                    first = pair.Key; firstWeight = pair.Value;
                }
                else if (pair.Value > secondWeight || pair.Value == secondWeight && pair.Key < second)
                { second = pair.Key; secondWeight = pair.Value; }
            }
            float total = firstWeight + secondWeight;
            return Vert(_verts[a] * (1f - t) + _verts[b] * t, default,
                first, second, total > 0f ? secondWeight / total : 0f);
        }

        private void EmitInlay(List<int> polygon, List<int> destination, HashSet<(int A, int B, int C)>? skirt)
        {
            for (int i = 1; i + 1 < polygon.Count; i++)
            {
                int a = polygon[0], b = polygon[i], c = polygon[i + 1];
                var area = Cross(_verts[b] - _verts[a], _verts[c] - _verts[a]);
                if (InlayDot(area, area) < 1e-16f) continue;
                destination.Add(a); destination.Add(b); destination.Add(c);
                skirt?.Add((a, b, c));
            }
        }

        private static void AddInlayVertex(List<int> polygon, int vertex)
        {
            if (polygon.Count == 0 || polygon[polygon.Count - 1] != vertex) polygon.Add(vertex);
        }

        private static void CloseInlay(List<int> polygon)
        {
            if (polygon.Count > 1 && polygon[0] == polygon[polygon.Count - 1]) polygon.RemoveAt(polygon.Count - 1);
        }

        private static float InlayDot(WearableVec3 a, WearableVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
}
