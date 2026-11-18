namespace Lumora.Domain.Enums;

/// <summary>Verfahren von „Zusammenfügen“ (ADR-0078 Punkt 5/11); Stufe 1 baut nur <see cref="Panorama"/>.</summary>
public enum MergeMethod
{
    Panorama = 0,
    ExposureFusion = 1,
    Denoise = 2,
    RemovePeople = 3,
    LongExposure = 4,
    FocusStack = 5,
    GroupShot = 6,
    HdrPanorama = 7,
    Timelapse = 8,
}

/// <summary>Aufnahmeart eines Panoramas (ADR-0078 Punkt 6).</summary>
public enum PanoramaProjection
{
    Auto = 0,

    /// <summary>Kugel 360° — als Horizontstreifen oder 2 : 1-Rundum-Bild.</summary>
    Spherical360 = 1,

    Cylindrical = 2,
    Perspective = 3,
    Vertical = 4,

    /// <summary>Nur verschieben (Scans, flach).</summary>
    ShiftOnly = 5,
}
