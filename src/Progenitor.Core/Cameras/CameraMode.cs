namespace Progenitor.Cameras;

/// <summary>
/// The local camera's mode. Values are the game's <c>ProjectM.CameraType</c> (client
/// metadata) and are checked against the metadata through <see cref="Layout"/>.
/// </summary>
public enum CameraMode
{
    /// <summary>No camera is assigned, or the client world does not exist.</summary>
    None = 0,

    /// <summary>The normal gameplay camera.</summary>
    TopDown = Layout.CameraTypeTopDown,

    /// <summary>Orbits an entity or a point (<c>ctx.Camera.Orbit</c>).</summary>
    Orbit = Layout.CameraTypeOrbit,

    /// <summary>Free-flying camera driven by mouse and keyboard (<c>ctx.Camera.FreeShot</c>).</summary>
    Free = Layout.CameraTypeFree,

    /// <summary>The free camera with its input switched off (the game sets <c>FreeCameraState.StaticCamera</c>).</summary>
    Static = Layout.CameraTypeStatic,
}
