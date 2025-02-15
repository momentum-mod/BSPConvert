namespace BSPConvert.Lib;

using System.Collections.Generic;
using LibBSP;
#if UNITY
using Vector3 = UnityEngine.Vector3;
#elif GODOT
using Vector3 = Godot.Vector3;
#elif NEOAXIS
using Vector3 = NeoAxis.Vector3F;
#else
using Vector3 = System.Numerics.Vector3;

#endif

public class HullConverter
{
    /// <summary>
    /// Converts the specified face vertices into a convex polygonal hull using the gift wrapping algorithm
    /// </summary>
    public static List<Vertex> ConvertConvexHull(Vertex[] faceVerts, Vector3 faceNormal)
    {
        // Treat face vertices as an arbitrary set of points on a plane and use the gift wrapping algorithm to generate a convex polygon
        var hullVerts = new List<Vertex>();

        Vertex pointOnHull = GetFurthestPointFromCenter(faceVerts);
        Vertex endPoint;
        do
        {
            hullVerts.Add(pointOnHull);
            endPoint = faceVerts[0];
            for (int j = 1; j < faceVerts.Length; j++)
            {
                if (
                    endPoint.position == pointOnHull.position
                    || IsLeftOfLine(pointOnHull, endPoint, faceVerts[j], faceNormal)
                )
                    endPoint = faceVerts[j];
            }

            pointOnHull = endPoint;
        } while (endPoint.position != hullVerts[0].position && hullVerts.Count < faceVerts.Length);

        return hullVerts;
    }

    private static Vertex GetFurthestPointFromCenter(Vertex[] faceVerts)
    {
        var center = new Vector3();
        foreach (Vertex vert in faceVerts)
            center += vert.position;

        center /= faceVerts.Length;

        var furthestPoint = new Vertex();
        float furthestDist = 0f;
        foreach (Vertex vert in faceVerts)
        {
            float distance = Vector3.Distance(vert.position, center);
            if (distance > furthestDist)
            {
                furthestPoint = vert;
                furthestDist = distance;
            }
        }

        return furthestPoint;
    }

    private static bool IsLeftOfLine(Vertex pointOnHull, Vertex endPoint, Vertex vertex, Vector3 faceNormal)
    {
        Vector3 a = endPoint.position - pointOnHull.position;
        Vector3 b = vertex.position - pointOnHull.position;
        var cross = Vector3.Cross(a, b);

        // Use face normal to determine if the vertex is on the left side of the line
        // TODO: Handle collinear vertices (dot product should be 0)
        return Vector3.Dot(cross, faceNormal) > 0;
    }
}
