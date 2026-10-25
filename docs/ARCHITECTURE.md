# Funkciók és felelősségi határok

A 2026-10-25-i átnézés célja az ismétlődő algoritmusok összevonása és a
memóriabeli, illetve fájlból olvasott feldolgozás közös számítási alapjának kialakítása.

## Funkciótérkép

| Terület | Felelős | Határ |
| --- | --- | --- |
| ER Mapper fejléc és vektorfájl | `ErMapper`, `Vectors`, `ErsDocument`, `ErvDocument` | Formátum, metaadat, dokumentuméletciklus; nincs felületi logika. |
| BIL bináris adat | `BilCodec`, `BilRasterReader`, `BilRasterWriter`, `RasterSource` | Egyetlen cellakódoló; a teljes és az ablakos olvasás ezt használja. |
| GeoTIFF | `RasterField.Gdal` | Natív GDAL-függőség kizárólag az adapterben; Core nem függ tőle. |
| Raszterablak és előnézet | `RasterReadWindow` | Közös határmetszés és kimeneti méret a memória-, BIL- és GDAL-adapterhez. |
| Geometriai leképezés | `RasterGeoReference` | Pixel–világ transzformáció; a funkciók nem végeznek vetületi átszámítást. |
| Kivágás, mozaik, hézagkitöltés | `RasterClipper`, `RasterMosaic`, `NoDataFiller`, `ConvexHull` | Eltérő műveletek; a kivágó megtartja a kért méretet és NoData-val tölti a kilógást, az olvasó csak a metszetet adja. |
| Profil és interpoláció | `RasterProfiler`, `BezierPatchInterpolator` | Egy bilineáris súlyozás és egy polivonal-bejárás; a Bézier külön interpolációs módszer. |
| Domborzat és szűrés | `TerrainAnalysis`, `ConvolutionFilter`, `ReliefShader` | Terepi deriváltak, képi szűrők és színes relief külön fogalmak; a relief a közös hillshade-et hívja. |
| Raszteralgebra | `RasterAlgebra` | Kifejezésnyelv és sávonkénti kiértékelés; nem felületi képletkezelő. |
| Statisztika | `SampleStatistics`, `RasterStatistics` | Közös Welford/Chan átlag- és szórásakkumulátor, összevonható részeredményekkel. |
| Zónák és változáselemzés | `PolygonCellRegion`, `ZonalStatistics`, `RasterChangeAnalysis` | Egy poligonraszterizálás és csempebejárás; külön eredmény a zónaérték, illetve a különbség/térfogat. |
| Vízrajz | `D8FlowGraph`, `HydrologyAnalysis`, `StreamNetwork` | Egy D8-kódolás, gráfépítés és ciklusellenőrzés; ráépül a gyűjtőérték és a Strahler-rend. |
| Izovonal és geometriai mérés | `ContourGenerator`, `Measurement` | Marching squares, vonalsimítás és geometriai hossz/terület. |
| Megjelenítés | `Rendering`, `RasterView`, `RasterLayer`, `VectorLayer` | Paletta és pixelek Core-ban; Avalonia-erőforrások, nézet és rétegek a felületben. |
| Parancsok és ablakok | `MainWindow.Commands`, `MainWindow.Gis`, `MainWindow.Filters`, `DialogControls` | Egy parancsmodell a natív és ablakon belüli menühöz; közös gombsorépítő. |
| Projekt és visszavonás | `MainWindow.Project`, `LayerRecipe`, `RasterView.History` | Mentett projekt/recept és futásidejű előzmények külön felelősségi körben. |

A `MainWindow` és `RasterView` részfájljai egy-egy felületi osztályt alkotnak;
a fájlokra bontás nem hoz létre új szolgáltatásréteget. Az algoritmusok Core-ban
maradnak, a menük és dialógusok azokat hívják.

## Közös számítási szabályok

- A minták a cellaközéppontban vannak. A bilineáris interpoláció a hiányzó
  sarkokat kihagyja, és a megmaradó súlyokat normalizálja.
- A poligon páros–páratlan szabállyal töltődik. A sorintervallum balról zárt,
  jobbról nyitott; a függőleges intervallum szintén félig nyitott. Közös határ
  és nulla szélességű önmetszési intervallum nem számolhat cellát kétszer.
  Ez pontosítja a korábbi, jobboldali határt is beleszámító működést.
- A poligonhatárt a raszterhez kell korlátozni az egész számmá alakítás előtt,
  így a távoli, de véges koordináták sem csordulnak túl.
- Az eltérés `double` pontossággal képződik és kerül a statisztikába.
  A materializált különbségraszter továbbra is `float`, ezért tárolási
  kerekítése eltérhet a nagyobb pontosságú összesítésétől.
- A D8 bemenet 0 vagy egyetlen iránykód: 1, 2, 4, 8, 16, 32, 64, 128.
  Érvénytelen kód és ciklus hibát jelez. Hiányzó iránycellába nem vezet él.
- A vízhálózat feldolgozása topológiai sorrendet követ; nem támaszkodik a
  `float` gyűjtőértékek szigorúan növekvő voltára.
- A teljes raszter és az ablakos forrás közös matematikát használ. A külön
  I/O- és párhuzamos végrehajtási útvonalak indokoltak: eltérő memória- és
  lemezelérési igényeket szolgálnak, nem külön algoritmusváltozatok.

## Ellenőrzés

```sh
dotnet test RasterField.slnx --no-restore
python3 scripts/check_duplicate_blocks.py
dotnet list RasterField.slnx package --vulnerable --include-transitive --no-restore
```

A duplikációellenőrző a produkciós C# forrásban keres legalább 12 egymást követő,
azonos, nem üres sort, a megjegyzések és `using` sorok nélkül. A tesztadatok és
generált források kimaradnak. Ez ismételhető ellenőrzés a szó szerinti másolatok
ellen; átnevezett vagy szerkezetileg eltérő, azonos célú kódhoz továbbra is review kell.

A regressziós tesztek a nagy poligonokat, önmetszési határesetet, nagy alapérték
melletti kis eltérést, D8 ciklust/hibás kódot, azonos gyűjtőérték melletti ágrendet,
profilt a csempehatáron, NoData-s kivágást és túlméretes olvasási ablakot fedik le.
