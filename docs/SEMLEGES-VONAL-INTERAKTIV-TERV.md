# Interaktív semlegesvonal-felkereső – elemzés és megvalósítási terv

Állapot: az elfogadott interaktív munkafolyamat megvalósítva és ellenőrizve. Dátum: 2026-11-13.

## Megvalósítási állapot

- Közös `Interpolation` modul: bilineáris, Bézier és köbös B-spline mintavétel, világkoordinátás adapter, ablakos forrás és általános raszterfinomítás. A meglévő Bézier- és profil-belépési pontok ezt használják.
- Teljes körön végzett jelöltkeresés, belső terepminták ellenőrzése, fordulási/önmetszési figyelmeztetések, kattintásos szerkesztés, változtatható lépéshossz, undo/redo és szünet/folytatás.
- Opcionális cél térképpel vagy koordinátával, állítható érkezési sugár, céltávolság és célszint-átlépés jelzése. A célkör elérése az elfogadott végpontra vonatkozik; nincs középpontba igazítás.
- Folyamatos vezetés 400 ms-os ütemmel. Megáll figyelmeztetés, érvénytelen/nem létező jelölt, elért cél, célszint-átlépés, közeli kurzor, térképi kattintás, eszközváltás, fókuszvesztés vagy a térkép elhagyása esetén. Az ütem nem párhuzamosít számításokat és nem fogad el figyelmeztetést önállóan.
- Elnevezhető, kapcsolható változatok közös kezdőponttal és külön meredekséggel/céllal; hossz, céltávolság, maximális terepeltérés és figyelmeztetésszám összehasonlítása.
- Meglévő törtvonal kiválasztása és vizsgálata az eredeti töréspontokon, geometriai módosítás nélkül. Tárolt referencia-meredekség, ennek hiányában kezdő–végponti átlag; a szakaszmeredekség 0,25 százalékpontos eltérése külön jelzés. Az ellenőrzési sorra kattintás kiemeli a szakaszt a térképen. Profil és CSV-export elérhető.
- A 2-es projektformátum beágyazva menti a próbavonalakat, változatokat, célokat, profilsorokat és redo-előzményt. Betöltéskor nincs újrakeresés és a folyamatos mód nem indul újra. Az 1-es projektek és a korábbi automatikus receptek továbbra is betölthetők.
- A mentett geometria hiányzó DEM esetén is megjeleníthető és továbbmenthető. Folytatáshoz az eredeti terepet/sávot kell visszatölteni: teljes mintatömbből és térbeli metaadatból képzett SHA-256 azonosító ellenőrzi az egyezést. Memóriabeli, recept nélküli DEM-et projektmentés előtt fájlba kell menteni.
- Az új UI kiváltotta az automatikus felületet. A régi számítások csak kompatibilitási célból maradtak meg.
- A meglévő hossz-szelvény zoom/pan és táblázatkezelési funkciói változatlanok. A felkereső a meglévő profilvezérlőt használja, annak interakcióit nem írja át.

A megvalósított interpolációs API leírása és példái: [Közös interpoláció](INTERPOLACIO.md).

### Használat

1. Válassz terepréteget és sávot, majd a **Semleges vonal → Interaktív** fület. Add meg az előjeles meredekséget és lépéshosszt, majd jelölj ki kezdőpontot.
2. A körön felkínált irányokat az egér vagy a nyílbillentyűk választják. Kattintás vagy Enter elfogad egy szakaszt. A szakmai figyelmeztetést külön gombbal lehet elfogadni; hiányos terepadatú szakasz nem fogadható el.
3. Ctrl+Z/Y visszavon/újraalkalmaz; Esc szüneteltet. A **Folyamatos rajzolás** gomb után a térképen távolabbi kurzorral vezethető a vonal. A figyelmeztetés megállítja ezt a módot.
4. A **Tervezési eszközök** részben érhető el a koordinátás kezdőpont, az opcionális cél, az új változat és a meglévő vonal ellenőrzése. A változatok névvel, kapcsolható megjelenítéssel és összehasonlító adatokkal szerepelnek.
5. A projekt befejezetlen munka közben is menthető. Visszanyitás után a **Tervezési eszközök → Próbavonal megnyitása**, majd **Folytatás** kapcsol vissza a szerkesztéshez. Hiányzó forrás esetén előbb töltsd be és válaszd ki az eredeti DEM-et.
6. A **Lezárás → új vektorréteg** áttekintést mutat, majd az elfogadott töréspontokkal létrehozza a vektorréteget. A réteg a szokásos exportokkal menthető; a projekt a részletes tervezési adatokat is megőrzi.

### Ellenőrzés és gyakorlati korlátok

- 422 sikeres teszt: analitikus és NoData-esetek, változó lépéshossz, undo/redo mentése, független változatok/célok, folyamatos mód leállása, forrásváltozás, DEM nélküli geometria és projektkompatibilitás. A Core mindkét célkeretrendszerre fordul.
- Élő Windows-felületi próba: kezdőpont, jelöltválasztás, szakaszelfogadás, célkör kijelölése és elérése; a hosszú feliratok keskeny panelhez igazítva, az alapműveletek rögzített alsó vezérlősorban.
- A mellékelt `P_00_01` raszteren, 16 kezdőpont és 58 jelölt vizsgálatakor a keresés és szakaszellenőrzés átlaga 1,37 ms, maximuma 8,08 ms volt (Intel Core i7-8700, Windows, .NET 10, Debug). A vizsgált pontok B-spline/bilineáris magasságkülönbségének maximuma 0,008 m. Ez a mellékelt, 1000 m-es cellájú adatra vonatkozó ellenőrzés, nem általános teljesítmény- vagy tereppontossági garancia.
- A B-spline simított felület; a bilineáris és Bézier-felület magasságai eltérhetnek. A kiválasztott módszer az elmentett adatok része.
- A terepellenőrzés mintavételes (legfeljebb 1 m vagy fél cella köz, maximum 8192 részszakasz). A minták közötti eltérésre nem ad analitikus korlátot; a kapacitáshatárt elérő szakasz nem minősül ellenőrzöttnek.
- A 0,5–8%-os meredekség és a 20 m-es lépés tartományán kívül tájékoztató jelzés jelenik meg. Ezek oktatási/tervezési útmutatások, nem általános műszaki megfelelőségi előírások. A geometriai figyelmeztetések elfogadása ettől külön történik.

## 1. Cél és vizsgálati alap

A jelenlegi automatikus semlegesvonal-kereső felhasználói munkafolyamatát interaktív, felhasználó által vezetett felkeresés váltsa fel. A számítás ajánlja fel a lehetséges következő tereppontokat, a felhasználó választ irányt, és menet közben látja döntése következményeit.

Vizsgált helyi források:

- `D:/Repositories/Primusz/zeroline_edu`, HEAD: `39b4409`, pluginverzió: 1.7.0. Kézikönyv, képernyőképek, `plugin.py`, `dialog.py`, `map_tool.py`, `terrain.py`, `roots.py`, `rules.py`, `terrain_check.py`, `warnings.py`, valamint a fejlesztési ötletek.
- RasterField, HEAD: `7016ab9`, a vizsgálatkor meglévő munkapéldány-módosításokkal együtt. Elsősorban `MainWindow.NeutralLine.cs`, `MainWindow.NeutralRoute.cs`, `NeutralLineTracer*`, `ProjectDocument.cs`, `MainWindow.Project.cs`, a kapcsolódó tesztek és dokumentáció.

Az eredeti elemzés forráskód- és dokumentációvizsgálat volt. A QGIS-plugin élő futtatása nem történt meg; a RasterField azóta elvégzett ellenőrzéseit a fenti megvalósítási állapot rögzíti. A plugin képernyőképei a kézikönyv szerint 1.6.0-s állapotot mutatnak; a működés megállapításainál az 1.7.0-s forrás az irányadó. A meglévő munkapéldány-módosításokat ez a tervezés nem változtatja meg.

## 2. Mit tud a QGIS-plugin?

### Felhasználói folyamat

1. DEM és magassági sáv kiválasztása.
2. Előjeles meredekség és vízszintes lépéshossz megadása.
3. Kezdőpont, opcionális célpont és érkezési sugár kijelölése térképen vagy koordinátával.
4. Lehetséges következő szakaszok megjelenítése; az egér irányához legközelebbi jelölt kiemelése.
5. Kattintásonkénti elfogadás, vagy időzített, egérrel irányítható folyamatos rajzolás.
6. Jobb kattintásra egy szakasz visszavonása. Más térképi eszköz használatakor szünet és visszatérési lehetőség.
7. Másik meredekség kipróbálása ugyanabból a kezdőpontból; az előző vonal szürke referenciaként megmarad a munkamenetben.
8. Elfogadás, eredményréteg, hossz-szelvény és mentési lehetőség.

A meredekség egy próbavonalon belül rögzített; a lépéshossz változtatható. A célkör elérése nem jelenti a célpont közepéhez való geometriai csatlakozást. A folyamatos mód célba éréskor, figyelmeztetés előtt, irányhiánynál és eszközváltáskor megáll. A dinamikus, kurzortávolságtól függő sebesség csak fejlesztési ötlet, nincs megvalósítva.

### Számítás

Az aktuális P pontból, h lépéshosszal és g százalékos meredekséggel a következő pont feltételei:

```text
Q(θ) = P + h · (cos θ, sin θ)
z_cél = z(P) + h · g / 100
F(θ) = z_DEM(Q(θ)) − z_cél = 0
```

A plugin a kör mentén kezdetben 1°-os szögközzel mintáz, az előjelváltásoknál intervallumfelezéssel pontosít, és helyi minimumkereséssel érintő megoldásokat is keres. Több mint két jelölt is lehetséges. A keresés nem bizonyítja az összes gyök megtalálását; keskeny megoldások kimaradhatnak.

A terepmagasságot köbös B-spline felületről veszi, GDAL-mintavétellel vagy belső 4×4-es súlyozással. Ellenőrzi a környezet érvényességét, a maszkot és a NoData-adatokat, valamint kezeli a sáv skáláját és eltolását. A B-spline simítja a magassági felületet; nem azonos a RasterField bilineáris felületével vagy a meglévő Bézier/Catmull–Rom interpolátorával.

### Ellenőrzés

Közös szabályok jelzik a nagy fordulást, visszafordulást, cikcakkot, nagy vagy kis meredekséget, hosszú lépést, terepeltérést és célhoz kapcsolódó problémákat. Kiinduló küszöbök:

| Vizsgálat | Plugin beállítása |
|---|---|
| Terepeltérés | 0,50 m felett figyelmeztet |
| Jelentős fordulás | legalább 45° |
| Visszafordulás | 90° felett |
| Cikcakk | két legalább 30°-os, ellentétes előjelű fordulás |
| Meredekség | abszolút értékben 0,5% alatt vagy 8% felett |
| Lépéshossz | 20 m felett, 0,01 m numerikus ráhagyással |
| Meglévő vonal szakaszmeredeksége | referenciaértéktől 0,25 százalékpontnál nagyobb eltérés |

A meglévő vonal ellenőrzése annak eredeti töréspontjait használja. A referencia a tárolt meredekség, ennek hiányában a teljes hosszra számított kezdő–végponti átlag. Az eredeti geometriát nem módosítja.

### Amit érdemes javítani az átvétel során

- A hosszú űrlap és a sok egyszerre látszó művelet helyett az aktuális feladathoz igazodó panel szükséges.
- A szakaszonként felugró figyelmeztető ablak megszakítja a térképi munkát. A döntést a térkép mellett, az érintett jelölt kiemelésével érdemes kezelni.
- A térképi figyelmeztetések kattintható magyarázata a pluginban még fejlesztési ötlet.
- A `terrain_check.py` kihagyja a nem mintázható belső pontokat, és belső minta nélkül is nulla eltérést adhat. Az új modul különböztesse meg az ellenőrzött, a részlegesen ellenőrzött és a nem ellenőrizhető szakaszt.
- A `plugin.py` interaktív önmetszésvizsgálata `ENABLE_SELF_INTERSECTION_CHECK = False` kapcsoló mögött van. Ez nem tekinthető alapból működő interaktív védelemnek; az új modulban közös vizsgálat szükséges.
- Változó lépéshossz mellett az eredmény egyetlen `step_m` mezője nem írja le az összes elfogadott szakaszt. Szakaszonkénti adatmegőrzés kell.
- A numerikus gyökpontosság nem a DEM tényleges terepi pontossága. A felület ne kommunikáljon megtévesztő pontosságot.

## 3. Eltérés a jelenlegi RasterFieldtől

| Szempont | Jelenlegi RasterField | Tervezett modul |
|---|---|---|
| Alapművelet | Két ág automatikus követése vagy A–B keresés | Következő szakasz felhasználói kiválasztása |
| Fő paraméter | Meredekség, maximális hossz | Meredekség, lépéshossz |
| Cél | Kétpontos módban pontos végpont | Opcionális tájékozódási cél és érkezési kör |
| Terep | Szigorú bilineáris felület | Külön definiált, következetesen használt mintavétel |
| Terepeltérés | Elfogadást korlátozó tűrés | Mérhető, látható szakmai figyelmeztetés; külön numerikus tolerancia |
| Eredmény | Automatikusan létrejövő származtatott réteg | Szerkeszthető munkavonal, tudatos lezárás |
| Megőrzés | Forrásból és receptből újraszámolható | Tényleges döntések és töréspontok mentése |

A jelenlegi bilineáris motor teljes szakaszra vonatkozó, cellánkénti ellenőrzése értékes. Ezt nem szabad észrevétlenül mintavételes ellenőrzéssel egyenértékűnek tekinteni. A régi 0,02 m-es magassági tűrés és a plugin 0,50 m-es figyelmeztetési határa eltérő szerepű érték.

## 4. Javasolt felhasználói élmény

### Munkatér

A jelenlegi „Egy pontból / Két pont között” választót a **Tervezés / Ellenőrzés** feladatválasztó váltja fel. A térkép marad a fő munkafelület, mellette kompakt vezérlőpanel, alul kinyitható élő hossz-szelvény. Az elfogadás és visszavonás görgetés nélkül elérhető.

**Előkészítés:** látható DEM és sáv, meredekség számmal és emelkedés/lejtés értelmezéssel, lépéshossz, kezdőpont, opcionális cél. A koordinátás bevitel kinyitható részben legyen; a szokásos indulás térképi kattintás. A speciális numerikus beállítások ne foglalják el az alapnézetet.

**Vezetés:** az előkészítési panel összecsukódik egy rövid összefoglalóvá. Látható az aktuális lépéshossz, teljes hossz, szintkülönbség és cél esetén annak távolsága. Az aktív jelölt külön kártyája mutatja a következő szakasz hosszát, magasságváltozását, fordulását és terepeltérését.

**Áttekintés:** lezárás előtt összegzés a vonalról, a célállapotról és az elfogadott figyelmeztetésekről. Innen egyértelműen vissza lehet térni a vezetéshez vagy létrehozni az eredményt.

### Térképi interakció

- Halvány keresőkör és jelöltek; az aktív szakasz erős kiemelése, végpontjelöléssel. Az érvényes jelöltek közti választás irányszög alapján történik.
- A kiemelés kis hiszterézist kapjon, hogy a két jelölt közti határon ne ugráljon. Az aktuális végponthoz nagyon közeli kurzornál maradjon meg az előző választás.
- Bal kattintás: elfogadás. `Ctrl+Z` és külön gomb: visszavonás; `Ctrl+Y`: ismételt végrehajtás. A jobb kattintás csak akkor szolgáljon visszavonásra, ha nem ütközik a RasterField térképi kezelésével.
- `Esc`: szünet, a munkavonal megtartásával. Pan és zoom használható; eszközváltás szüneteltet, a visszatérés folytatható állapotot ad.
- Billentyűzettel is választhatók a jelöltek. Szín mellett vonalstílus, jel és szöveg különböztesse meg az állapotokat.
- Célkör elérésekor jól látható érkezési jelzés. Automatikus odarántás ne módosítsa a meredekséget; pontos végponti csatlakozás későbbi, külön ellenőrzött művelet lehet.

### Élő visszajelzés

A hossz-szelvény külön mutassa a tervezett egyenest és a köztes terepmintákat. A diagramra mutatás jelöljön helyet a térképen; egy figyelmeztetés kiválasztása emelje ki a szakaszt és a profil megfelelő részét.

Az általános tanácsok, például a hosszú lépés, nem állítják meg a munkát. A problémás jelölt elfogadása előtt helyben jelenjen meg a mért érték és egy konkrét lehetőség: **Kisebb lépéssel újraszámolás**, **Másik irány**, **Elfogadás figyelmeztetéssel**. Az utóbbi döntés a szakaszhoz kötve megmarad. Nem mintázható terepet vagy érvénytelen numerikus eredményt nem lehet egyszerű figyelmeztetésként felülbírálni.

Ha nincs következő irány, az üzenet „Ezekkel a beállításokkal nem találtunk következő pontot”, ne bizonyított lehetetlenséget állítson. Ajánlott műveletek: lépéshossz módosítása, visszavonás, új változat más meredekséggel. A program ne változtasson észrevétlenül lépéshosszt vagy meredekséget.

### Folyamatos rajzolás és változatok

A folyamatos mód a kattintásos működés stabilizálása után kerüljön be. Indítás/szünet egyértelmű vezérlővel; automatikus megállás célba éréskor, figyelmeztetésnél, NoData esetén, fókuszvesztéskor, eszközváltáskor és a kurzor közelében. Az első verzióban rögzített ütemű, később kipróbálható a kurzortávolságtól függő sebesség.

Az **Új változat** ugyanarról a kezdőpontról, új meredekséggel indul. Az előző változat ne vesszen el; névvel, kapcsolható térképi megjelenítéssel és összehasonlítható hosszal, cél-távolsággal, maximális terepeltéréssel és figyelmeztetésszámmal maradjon meg. Egy adott változat meredeksége rögzített.

## 5. Számítási és adatkezelési döntések

**Elfogadott architekturális követelmény:** az interpolációs és felületfinomítási eljárások közös, általánosan használható Core-modulba kerüljenek. A meglévő bikubikus Bézier-eljárás mellé ebben a modulban készüljön köbös B-spline mintavevő, a pluginhoz hasonló folyamatos felülettel. A semlegesvonal-felkereső ennek fogyasztója legyen. A régi bilineáris motor maradjon meg a régi projekt-receptek visszaállítására. Az új felkereső alapértelmezett módszerét azonos DEM-en végzett összehasonlítással véglegesítsük; a B-spline nem puszta vizuális simítás, hanem más magassági felület.

### Közös interpolációs és újramintavételezési modul

A RasterFieldben már létezik a `BezierPatchInterpolator`: bikubikus Bézier-foltokat épít Catmull–Rom alapján becsült deriváltakkal, állítható feszítéssel, opcionális túllövéskorlátozással és NoData-kezeléssel. Nyilvános műveletei finomabb rasztert készítenek (`Subdivide`, `SubdivideWindow`), és tetszőleges pontot is mintavételeznek (`Sample`). A pontonkénti bilineáris mintavétel eredetileg részben a `RasterProfiler` osztályban található. Ezek közös modulba szervezése az új fejlesztés része; az első megvalósítás megtörtént.

Javasolt helye: `RasterField.Core/Interpolation`, önálló felelősségi körrel, Avalonia-, GDAL- és semlegesvonal-függőség nélkül. Az adatforrás a meglévő raszterforrás-absztrakcióhoz kapcsolódjon; a fájlformátum és az adattárolás ne az interpolátor feladata legyen. A modul általános skalár rasztersávokat kezeljen, ne csak DEM-magasságokat.

Két egymásra épülő szolgáltatás szükséges:

1. **Folytonos felület mintavétele:** adott helyen interpolált érték lekérdezése, teljes finomított raszter előállítása nélkül. Ezt használhatja a semlegesvonal-kereső, a hossz-szelvény és más elemzés.
2. **Új raszter előállítása:** célrácsra vagy finomítási tényező szerint újramintavételezés ugyanazzal a mintavevővel. Ezt használja a felületfinomítás felülete és a raszterexport. A célrács georeferenciáját és pixelközéppontjait is következetesen kezelni kell.

Közös módszercsalád: bilineáris, a meglévő bikubikus Bézier/Catmull–Rom, valamint köbös B-spline. A módszerspecifikus paraméterek külön típusokban maradjanak; a Bézier feszítése például ne jelenjen meg értelmetlen B-spline beállításként. A közvetlen B-spline súlyozás simító közelítés, nem feltétlenül halad át a bemeneti mintákon; ezt a módszerleírás és a tesztek is tükrözzék. A tervben B-spline szerepel, nem külön β-spline eljárás.

Közös szerződés:

- Egyértelmű pixelközéppont-konvenció; világkoordinátás adapter az általános mintavevő felett.
- Érték és érvényességi státusz; a NoData, raszterszél, extrapoláció és alacsonyabb rendű módszerre visszaesés explicit kezelése. A felhasznált visszaesés ne maradjon rejtett.
- A forrásadat, sávskála és eltolás kezelésének egyetlen definiált helye, kétszeres alkalmazás nélkül.
- Ablakos olvasás és korlátozott gyorsítótár nagy raszterekhez; megszakítható tömeges újramintavételezés. A párhuzamos használat szerződése legyen dokumentált.
- Módszerazonosító, verzió és beállítások mentése a feldolgozási receptbe.
- Ha egy fogyasztó gradiensre is igényt tart, az külön képességként legyen elérhető; a korlátozott vagy visszaeső felülethez nem szabad nem létező simasági garanciát társítani.

A semlegesvonal-felkereső szigorú érvényességi beállítást használhat, míg más feladat kérhet dokumentált extrapolációt vagy bilineáris visszaesést. Ezek felhasználási beállítások ugyanazon modulban, nem párhuzamos algoritmusmásolatok.

Átálláskor a meglévő Bézier és bilineáris belépési pontok kompatibilitási burkolóként megmaradhatnak. Először a felületfinomítást és a profilkészítést kell a közös megvalósításhoz kapcsolni, a korábbi viselkedésüket megőrizve. Az új semlegesvonal-modul ugyanezeket a szolgáltatásokat használja; más elemzések fokozatosan csatlakozhatnak.

### A semlegesvonal-modul további követelményei

- Ugyanaz a mintavevő szolgálja ki a pontmagasságot, jelöltkeresést, terepellenőrzést és profilt. A mintavétel típusa, verziója és a DEM azonosítója legyen az eredmény része.
- Világkoordinátában számított vízszintes hossz; elforgatott és nem négyzetes pixelek kezelése. A vízszintes és magassági egység ellenőrzése; hiányos egységadatnál látható, mentett feltételezés szükséges.
- A teljes kör jelöltjeit keressük; érintő gyök, 0°/360° átmenet, duplikációk, NoData-intervallumok és zérus meredekség külön eseteivel. Sík, azonos magasságú terepen a folytonos irányhalmaz külön kezelést igényel, nem több száz azonos értékű jelölt felsorolását.
- A rögzített meredekség numerikus célját a kezdőmagasság és az összesített hossz határozza meg, elkerülve a lépésenkénti gyökhiba felhalmozódását.
- Adaptív terepmintavétel, garantált belső mintákkal és érvényességi státusszal. A vizsgálat pontosságát és megszakítási korlátait rögzíteni kell; mintavételes eredményt ne nevezzünk teljes szakaszra bizonyított maximumnak.
- A geometriai/numerikus elfogadhatóság és a szakmai figyelmeztetés legyen külön eredmény. A plugin küszöbei induló ellenőrzési profilt alkothatnak, verziózott beállításként.
- Közös ellenőrző motor a tervezett és importált vonalakhoz, bekapcsolt önmetszésvizsgálattal. Külső vonalnál a referencia-meredekség forrása legyen látható és felülírható.
- Az interaktív vonal nem állítható vissza pusztán kezdőpontból, meredekségből és hosszból. Minden elfogadott töréspontot, szakaszhosszt, figyelmeztetést és felülbírálást tárolni kell.

## 6. Javasolt szerkezet a RasterFieldben

Az alábbi nevek új, tervezett komponensek, nem meglévő API-k.

| Komponens | Feladat |
|---|---|
| `ISurfaceSampler` és interpolációs megvalósításai | Közös Core-modul: bilineáris, Bézier és B-spline pontmintavétel, érvényesség és módszerleírás |
| `RasterResampler` | A közös felületmintavevővel finomított raszter készítése, célrács és georeferencia kezelése |
| Világkoordinátás felületadapter | Raszterkoordináták transzformációja; a semlegesvonal és más térbeli elemzések kapcsolata a közös mintavevővel |
| `NeutralCandidateSolver` | Egy lépés jelöltjei és a keresés státusza, UI-függőség nélkül |
| `NeutralLineValidator` | Irány, önmetszés, terepeltérés, célállapot és meglévő vonal ellenőrzése |
| `NeutralDesignSession` | Forrás, paraméterek, pontok, változatok, figyelmeztetések és undo/redo |
| `NeutralDesignController` | Aszinkron számítás, állapotváltások, megszakítás és eredmények alkalmazása |
| `NeutralDesignPanel` / térképi overlay | Állapotfüggő vezérlők, jelöltek, célkör és interaktív jelzések |

Az új állapotmodell: előkészítés → kezdőpont/cél kijelölése → vezetés ↔ szünet → áttekintés → lezárt eredmény. A jelöltszámítás folyamatban lévő állapota és a figyelmeztetés miatti döntési állapot is explicit legyen. A „nincs jelölt” javítható tervezési állapot, nem az egész munkamenet elvesztése.

Az egérmozgatás csak a kész jelöltek közül választ. Új jelöltszámítás elfogadás, visszavonás vagy paraméterváltás után kell. Minden kérés munkamenet- és revízióazonosítót kapjon; egy későn befejeződő feladat ne írhassa felül az újabb állapotot. DEM-, sáv- vagy forrásváltozás állítsa meg az aktív számítást, és ne értelmezze át automatikusan a meglévő pontokat.

A `MainWindow.NeutralLine.cs` nagy eseménykezelői helyett külön komponensekbe érdemes helyezni az új működést. A meglévő `ProfileChartControl`, térképi profiljelölés, vektorréteg-kezelés, lokalizáció és stílusrendszer újrahasznosítható.

### Mentés és átállás

A jelenlegi projektmentés kihagyhatja azokat a rétegeket, amelyeknek nincs fájlja és receptje. Ezt az új interaktív eredményekhez ki kell egészíteni. Javasolt verziózott projektadat a tervezési munkamenethez és változatokhoz, beágyazott pont- és szakaszadatokkal. A DEM továbbra is külső forrás; hiányakor a mentett geometria megjeleníthető, de folytatáshoz/újraellenőrzéshez forrás szükséges. A projektverzió és migráció külön feladat.

A régi `neutral-line` és `neutral-route` receptbetöltés maradjon működőképes. A régi automatikus módok az új felületen megszűnnek, a kompatibilitási kód ettől még megmarad. Az elfogadott új vonal rendes vektorréteg lesz; export a meglévő formátumkezeléshez igazodjon. A részletes tervezési metaadatok akkor is maradjanak meg a projektben, ha az exportformátum nem tudja őket tárolni.

## 7. Megvalósítási sorrend és készültségi feltételek

| Szakasz | Eredmény | Elfogadási feltétel |
|---|---|---|
| 1. UX-prototípus | Előkészítés, vezetés, elakadás, figyelmeztetés és lezárás bemutatása | A munkafolyamat végigkövethető; fő műveletek görgetés nélkül elérhetők |
| 2. Számítási alap | Közös interpolációs modul, meglévő Bézier/bilineáris eljárások integrálása, B-spline, körkereső és szakaszellenőrzés | A felületfinomítás és profil is a közös modult használja; korábbi viselkedés regressziós ellenőrzése, analitikus és szintetikus tesztek |
| 3. Első teljes folyamat | Kattintásos vezetés, opcionális cél, undo/redo, profil, lezárás és mentés | Új vonal elkészíthető, elmenthető és azonos pontokkal visszanyitható |
| 4. Teljes funkciókör | Folyamatos mód, változatok, meglévő vonal ellenőrzése, összekapcsolt figyelmeztetések | Eszközváltás és megszakítás adatvesztés nélkül; azonos szabályok a két feladatban |
| 5. Átállás és finomítás | Régi UI lecserélése, projektkompatibilitás, magyar/angol szövegek és dokumentáció | Régi projektek és új munkamenetek regressziós ellenőrzése, reprezentatív terepi használat |

A 3. szakasz működő részeredmény, nem a plugin teljes funkciókörének kész helyettesítése. A végleges csere a 4–5. szakasz eredményeivel teljes.

Fontos tesztek:

- Azonos módszer és beállítás mellett a közvetlen pontmintavétel és az újramintavételezett raszter célpixelközépponti értékei egyeznek a kimeneti adattípus pontosságán belül.
- A meglévő Bézier finomítás regressziós esetei: feszítés, túllövéskorlátozás, NoData, raszterszél, ablakhatár és georeferencia. A bilineáris fogyasztók korábbi viselkedése is megmarad.
- Teljes raszter és ablakos forrás mintavétele azonos eredményt ad; a gyorsítótár nem okoz eltérést. A B-spline simítás és a Bézier interpoláció külön elvárt eredményekkel tesztelendő.
- Ismert sík: analitikus jelöltek, előjelek, vízszintes hossz; nulla meredekség és folytonos megoldáshalmaz.
- Hullámos terep: több jelölt, érintő gyök, szögátmenet, numerikus hiba felhalmozódása.
- NoData és DEM-szél: érvényes végpontok közötti adathiány sem minősülhet nulla terepeltérésnek.
- Terepeltérés a töréspontok között; fordulási és önmetszési figyelmeztetések.
- Célkör elérése a középpontba rántás nélkül; a célszint korai átlépése.
- Undo/redo az összes kapcsolódó szakaszadattal; lépéshosszváltás és új meredekségű változat.
- Rétegváltás, törlés, fókuszvesztés, későn visszaérkező számítás; folyamatos mód megállítása.
- Projektmentés és visszanyitás változó lépéshosszal és felülbírált figyelmeztetéssel; régi receptbetöltés.
- Magyar/angol szövegek, billentyűzet, keskeny panel, eltérő DPI és kontrasztos térképi háttér.

Teljesítménycélként a kurzoros jelöltváltás legyen azonnali, a következő jelöltek számítása tipikus tesztadatokon lehetőleg 100 ms körüli vagy gyorsabb. Ez tervezési cél, nem jelenleg mért eredmény; referencia-DEM-ekkel és rögzített hardveren kell mérni. Lassabb számítás közben is maradjon használható a térkép és a szüneteltetés.

## 8. Elfogadott irány és a prototípusban véglegesítendő részletek

Az alapirányt a felhasználó elfogadta. Az alapértelmezett mintavétel és az interakciós részletek a prototípus és a numerikus vizsgálat alapján véglegesíthetők. Kötelező kiegészítés a közös, több feladathoz használható interpolációs modul.

1. A felhasználó által vezetett felkeresés váltja fel mindkét jelenlegi automatikus módot; régi projektek támogatása megmarad.
2. A B-spline legyen az új modul alapfelülete, összehasonlító numerikus vizsgálat után véglegesítve.
3. Kattintásos mód az alap; a folyamatos vezetés külön bekapcsolható.
4. Célpont opcionális, érkezési körrel; pontos végponti csatlakozás nem része az első kiadásnak.
5. A plugin oktatási küszöbei kiinduló ellenőrzési profilként szerepeljenek, ne általános műszaki megfelelőségi minősítésként.
6. A magasabb felhasználói élmény része a menthető, folytatható munkamenet és az összehasonlítható változatok.

A fenti munkafolyamat és projektintegráció elkészült; az alábbi eredeti terv a döntések és követelmények nyilvántartása marad. A forrásban jelzett GPL-2.0-or-later licenc és szerzői megjelölések rögzítendők az átvételi nyilvántartásban, ha tényleges kód vagy más licencelt anyag kerül átvételre; ez a terv saját RasterField-komponenseket ír le, forrásmásolást nem tartalmaz.
