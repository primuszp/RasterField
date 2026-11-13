# Közös felületmintavétel és raszterfinomítás

A `RasterField.Core/Interpolation` modul általános skalár rasztersávokon dolgozik.
Nem függ Avaloniától, GDAL-tól, mértékegységtől vagy a semlegesvonal-modultól.
A mintavevő nem készít előzetesen finomított rasztert: egy tetszőleges pont értékét
közvetlenül számítja ki. Ugyanezekkel a mintavevőkkel raszter is előállítható.

## Elérhető eljárások

| Factory | Működés | Hiányzó adatok és perem |
|---|---|---|
| `SurfaceSampler.Bilinear` | Bilineáris interpoláció | Alapból szigorú; `strict: false` esetén a megmaradó súlyokat újranormalizálja és ezt jelzi. Nulla súlyú szomszéd hiánya nem érvényteleníti a mintát. |
| `SurfaceSampler.Bezier` | A meglévő bikubikus Bézier/Catmull–Rom felület | A korábbi feszítés, túllövéskorlátozás és NoData-beállítások megmaradnak. Alapból peremi tükrözést alkalmaz; `strictBoundary: true` letiltja ezt. |
| `SurfaceSampler.BSpline` | Közvetlen köbös B-spline súlyozás | Teljes, érvényes 4×4-es környezet kell; a peremet és adathiányt nem tölti ki. |

A B-spline simítja a mintákat, és általában nem halad át az eredeti cellaértékeken.
A `Factor = 1` ezért B-spline esetén is módosíthatja az értékeket. A Bézier és a
B-spline eltérő felületet határoz meg; azonos bemenetnél sem kell azonos értéket adniuk.
Egyik eljárás sem állít elő új mért információt.

## Pontmintavétel és hossz-szelvény

```csharp
using RasterField.Interpolation;
using RasterField.Rasters;

ISurfaceSampler surface = SurfaceSampler.BSpline(raster);

// Pixelkoordináta: az (oszlop, sor) cella közepe (oszlop+0.5, sor+0.5).
SurfaceSample result = surface.Sample(12.25, 30.75);
if (result.IsValid)
{
    double value = result.Value.Value;
    // A Flags mutatja az esetleges visszaesést/újranormalizálást/extrapolációt.
}

var world = new WorldSurfaceSampler(surface, geoReference);
SurfaceSample elevation = world.Sample(worldX, worldY);

var profile = RasterProfiler.SamplePolylineWorld(
    surface, geoReference, vertices, spacing: 2.0);
```

A `SurfaceSample.Value` double pontosságú. Az érvénytelen eredmény `null`, a
`Flags` különbözteti meg az adathiányt, hiányos környezetet, lefedettségen kívüli
pontot és érvénytelen koordinátát. Érvényes eredménynél is lehetnek jelzések:
`Renormalized`, `Extrapolated`, `FallbackBilinear`. Az `IsValid` önmagában nem
garantál teljes és eredeti módszerrel mintavételezett környezetet.

A világkoordinátás adapter ellenőrzi a méretek egyezését és a véges, invertálható
affin transzformációt. Nem végez vetületi átszámítást vagy mértékegység-konverziót.
A skálát és eltolást a forrásadapternek kell már alkalmaznia; a modul a kapott
sávértékeken dolgozik, nem alkalmaz rájuk újabb skálázást.

## Finomított raszter előállítása

```csharp
var world = new WorldSurfaceSampler(SurfaceSampler.BSpline(raster), geoReference);
ResampledRaster fine = RasterResampler.Subdivide(world, factor: 4,
    cancellationToken: cancellationToken);

Raster values = fine.Raster;
RasterGeoReference outputGrid = fine.GeoReference;

// Másik célrács, azonos koordináta-rendszerben:
ResampledRaster target = RasterResampler.Resample(world, targetGeoReference,
    cancellationToken: cancellationToken);
```

A finomítás megtartja az eredeti területi lefedettséget, elforgatást és pixelirányt.
A kimenet `float` raszter, az érvénytelen minták helyén NaN áll. A kimeneti raszter
önmagában nem tárolja az összes pontmintavételi diagnosztikát. Szakmai ellenőrzéshez
a közvetlen `SurfaceSample` eredményt kell használni. A célrácsos eljárás
pontmintavétel; durvább felbontásra történő átalakításhoz nem helyettesít
területátlagoló vagy aliasinget csökkentő módszert.

## Nagy adatok és párhuzamos használat

Mindhárom factory fogad `IRasterSource` bemenetet is:

```csharp
ISurfaceSampler surface = SurfaceSampler.BSpline(source,
    band: 0, tileSize: 256, cancellationToken: cancellationToken);
```

A közös LRU-gyorsítótár legfeljebb 16 forráscsempét tart meg. A módszerek a teljes
forrás koordinátáival dolgoznak, ezért a csempehatár nem lesz mesterséges
felülethatár. A forrás a hívó tulajdona; maradjon nyitva és változatlan a mintavétel
alatt. Egy mintavevőn egyszerre egy hívó dolgozhat. Párhuzamos munkához külön
mintavevők és megfelelően szálbiztos vagy külön adatforrások szükségesek.

A Bézier-beállításokat a factory lemásolja, ezért későbbi UI-módosításuk nem
változtatja meg a már létrehozott felületet. A `MethodId` verziózott azonosító;
projektmentéskor emellett a kiválasztott paramétereket, perem/NoData-szabályokat,
sávot és adatforrást is meg kell őrizni.

## Kompatibilitás és készültség

A meglévő `BezierPatchInterpolator` publikus metódusai továbbra is használhatók.
A matematikai megvalósítás átkerült a közös modulba, az optimalizált teljesraszteres
Bézier-finítás ugyanazt a foltkiértékelést használja, mint a pontmintavétel.
A `RasterProfiler.BilinearSample` is kompatibilitási belépési pont lett.
A korábbi profilok megengedő NoData-kezelése megmaradt; nem véges forrásértékből
az új modul nem ad érvényes mintát.

A B-spline a Core API mellett az új interaktív semlegesvonal-felkereső alapfelülete.
Önálló általános felületi módszerválasztót még nem kapott.
A kattintásos munkamenet, a köralapú iránykereső és a szakaszellenőrzés is
a közös mintavételi felületet használja. A korábbi hossz-szelvény kezelése megmarad.
