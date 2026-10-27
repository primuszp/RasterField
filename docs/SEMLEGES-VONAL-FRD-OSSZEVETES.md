# Forest Road Designer és a RasterField semlegesvonal-követője

Forrásvizsgálat: 2026-10-27. A vizsgált Forest Road Designer-változat:
[`c7cecba977614c079936c7bd94ac71200aa4a5e9`](https://github.com/GobiernoLaRioja/forestroaddesigner/tree/c7cecba977614c079936c7bd94ac71200aa4a5e9),
a forrás szerint 2025-10-17-i commit. A megállapítások erre a változatra vonatkoznak;
a QGIS-bővítmény működését nem futtattuk. A RasterField-változatokat közvetlenül,
azonos raszterekkel és tűrésekkel hasonlítottuk össze.

## A két FRD-algoritmus

Az [`interactive_opt.py`](https://github.com/GobiernoLaRioja/forestroaddesigner/blob/c7cecba977614c079936c7bd94ac71200aa4a5e9/route_optimizer/interactive_opt.py)
interaktív szerkesztő: rögzített hosszúságú lépéshez a `candidates()` egy teljes kört
mintavételez (legalább 720 szögközzel). A `find_roots()` a kör magasságából kivonja
az aktuális ponthoz képest előírt magasságot. Az `approx_roots()` szigorú
előjelváltások között lineárisan közelíti a gyökhelyet. Több jelölt közül a kurzor
irányához legközelebbit választja. Alapértelmezésben az emelkedő és a lejtő
változatot is vizsgálja (`up_and_down=True`). A pontmagasságot a SciPy
`RegularGridInterpolator` adja, alapértelmezett lineáris interpolációval.

Ez hasznos geometriai szerkesztési elv, de a kódból következik két korlát:
a szigorú előjelváltás-vizsgálat kihagyhat pontosan mintaponton levő vagy érintő
gyököt; a megfelelő szakaszvégi magasság nem ellenőrzi a közbenső terepi profilt.
A felbontást egyetlen skalár írja le, ami négyzetes cellákra alkalmasabb a teljes
affin transzformációnál. Az interaktív konfiguráció átveszi az `exclusion_array`
argumentumot, de ebben a konfigurációs függvényben nem használja fel.

Az [`a_star.py`](https://github.com/GobiernoLaRioja/forestroaddesigner/blob/c7cecba977614c079936c7bd94ac71200aa4a5e9/route_optimizer/a_star.py)
ezzel szemben célpontra keres raszteres szomszédságon. A
[`heuristics.py`](https://github.com/GobiernoLaRioja/forestroaddesigner/blob/c7cecba977614c079936c7bd94ac71200aa4a5e9/route_optimizer/heuristics.py)
a szakaszvégi magasságkülönbségből számított abszolút minimum/maximum esést
vizsgálja; szigorú módban a nem megfelelő szomszédokat elveti. A
[`penalties.py`](https://github.com/GobiernoLaRioja/forestroaddesigner/blob/c7cecba977614c079936c7bd94ac71200aa4a5e9/route_optimizer/penalties.py)
az irány- és esésváltozást, az ívsugarat, a földmunkát és a kizárt területeket
büntetheti. Ezek a kétpontos, többfeltételes úttervezéshez érdekesek. Az előírt,
előjeles, állandó esésű vonal követését önmagukban nem helyettesítik.

## Összevetés és a beépített változtatások

| Szempont | FRD interaktív módszer | RasterField korábban | RasterField javítva |
|---|---|---|---|
| Következő pont | Körön több gyök közelítése | Egy helyi Newton-keresés | Gyors helyi korrekció, szükség esetén több körmetszés keresése |
| Gyök pontosítása | Szomszédos körminták lineáris közelítése | Newton a tényleges terepen | Newton vagy intervallumfelezés a tényleges terepen |
| Pontos és érintő gyök | A szigorú előjelváltás kihagyhatja | Kezdőbecsléstől függ | Mintaponti zérusok és stacionárius érintések külön vizsgálata |
| Továbbhaladás iránya | A kurzorhoz legközelebbi jelölt | Rögzített gradiensoldal | Az érkező irányhoz legközelebbi gradiensirány; a kezdeti két oldal megőrzése |
| Cellahatár | Nincs külön esemény | A felezés a határ előtt rekedhetett | Az élre külön kiszámított, esést teljesítő töréspont |
| Lépéshossz | Felhasználó által beállított sugár | Újrainduló negyedcellás lépés | Siker után 1,5-szeres növekedés, korlátozott maximális sugárral |
| Előírt profil | Aktuális ponthoz képest ± magasságlépés | Globális, előjeles célprofil | Globális, előjeles célprofil |
| Szakasz belseje | A gyökkeresés nem ellenőrzi | Másodfokú profil és deriváltak | Ugyanaz a teljes ellenőrzés, stabilabb közvetlen bilineáris deriváltakkal |
| Georeferencia és NoData | Skalár felbontás; interpolációs alapviselkedés | Teljes affin transzformáció; négy érvényes sarok | Megmaradt |

A körkeresés az egyenletes szögosztást a kör és a raszterfolthatárok metszéseivel
bővíti. Adathiányos íveken nem hoz létre gyökintervallumot. Az előjelváltást
intervallumfelezéssel pontosítja; a derivált előjelváltása érintést vagy két közeli
gyököt is jelezhet. A keresés véges szögfelosztást használ: nem bizonyítja az összes
elméletileg lehetséges folytatás megtalálását. Minden jelöltnek teljesítenie kell
a teljes szakasz magassági és helyi eséstűrését, és előre kell haladnia.

Az élmagasság egyenes mentén lineáris. Az esésfeltétel és a vízszintes távolság
összekapcsolásával másodfokú egyenlet adja a cellahatáron levő jelölteket. A négyzetre
emelés miatt keletkező hamis gyököket az eredeti előjeles egyenlet szűri ki. A 0%-os
esetet közvetlen lineáris egyenlettel oldjuk, elkerülve a numerikusan érzékeny
kettős gyököt. A határ túloldali gradiensét az érkezési irány alapján választjuk.

A helyi esést közvetlenül a bilineáris gradiensből és vegyes deriváltból számítjuk.
Ez elkerüli, hogy két közel azonos magasság különbségéből, egy nagyon rövid
határszakaszon mesterséges eséshiba keletkezzen. A tűrések nem változtak.
A C# megvalósítás saját kód; az FRD-ből a geometriai ötletet tanulmányoztuk,
forráskódot nem emeltünk át.

## Reprodukálható összehasonlítás

A régi változat a `98ae292` commit követője. Mindkét változat azonos cellaközépponti
Float32 rasztert, 0,02 m magassági és 0,1 százalékpontos eséstűrést kapott, ahol
külön nem jelöljük. A koordináták és távolságok itt méterben értendők.

| Eset | Régi követő | Javított követő |
|---|---|---|
| Hullámos terep, 5%, 1 m kért hossz | 0,204786 m / 0,318895 m után tűréshatár | Mindkét ág eléri az 1 m-t |
| Szintvonalkövetés, 0%, 10 m kért hossz | 0,200000 m / 0,800000 m után tűréshatár | Mindkét ág eléri a 10 m-t |
| 12,5%-os síklejtő, 5%-os vonal, 40 lépés | A 10 m helyett 7,071068 m, lépésszámkorlát | Eléri a 10 m-t |

A hullámos raszter képlete:
`z(x,y)=0.1*x+0.375*sin(y/6+4.6)+0.1*sin(0.9*x+2.8)`.
A kezdőpont `(50.2,50.3)`, a cellaméret 1 m, a raszter 100×100 cellás.
Az előjelváltott terep és −5%-os esés ugyanúgy része a tesztnek.

A szintvonalas raszter képlete:
`z(x,y)=0.01*((x-50)^2+(y-50)^2)`.
A kezdőpont `(70.2,50.3)`, a cellaméret 1 m, a helyi eséstűrés 0,05 százalékpont.
Mindkét esetet külön, sűrű `RasterProfiler`-mintavétel is ellenőrzi.

Natív macOS ellenőrzés: `P_00_01.ers`, `(532263.7846912544,229342.8991133728)`
kezdőpont, +0,2%, áganként 10 000 m. Mindkét ág elérte a kért hosszt. Az első ág
kijelzett legnagyobb hibái 0,0012 m és 0,0007 százalékpont; a másodiké 0,0005 m és
0,0003 százalékpont. A külön ablakos profilban az előírt és a terepi görbe együtt
látható. A második ág CSV-exportját külön visszaolvastuk.

A teljes tesztcsomag 358 sikeres tesztet tartalmaz, köztük 14 semlegesvonalhoz
kapcsolódó esetet. A projektből való újraszámítás és az ERV-visszaolvasás továbbra
is sikeres. A projekt receptekből az aktuális algoritmus számol újra, ezért régi
projekt megnyitásakor a korábban idő előtt leálló ágak hosszabbá válhatnak.
