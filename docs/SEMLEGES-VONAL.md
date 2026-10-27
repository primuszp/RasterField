# Semleges vonal – kezdőpontból induló követés

Az **Elemzés → Semleges vonal…** menü vagy a bal oldali **Semleges vonal** fül
nyitja az eszközt. Állítsd be az előjeles esést (pozitív: emelkedés; negatív:
lejtés), az ágankénti maximális hosszt, a magassági tűrést és a helyi eséstűrést.
A **Kezdőpont kijelölése** után kattints a terepre. Új kattintás új eredményrétegeket
hoz létre; a korábbi eredmények megmaradnak.

A két indulási irány külön színű, külön menthető vektorréteget ad. Az **1. ág / 2. ág**
választó a hozzá tartozó hossz-szelvényt és leállási okot mutatja. Az **Ablak…**
nagyítható profilnézetet nyit, a **CSV…** a tényleges terepmagasságot és az előírt
magasságot azonos szelvényezéssel exportálja. A diagram fölötti egérmutatóhoz térképi
helyjelző tartozik. A réteg mentésgombjával ERV export készíthető; a projektmentés
az eredeti raszterhez kötött számítási receptet is tárolja.

## Modell és pontosság

A követés a cellaközéppontokból képzett, **bilineáris felületen** történik.
A megjelenítési Bézier-simítás ezt nem módosítja. A hossz vízszintes, halmozott
vonalhossz: `z(s) = z0 + (esés / 100) * s`.

A gradiensből kapott két egységirány előrejelzi a következő pontot. A korrektor
rögzített vízszintes lépéshosszú körön keresi az előírt magasságot. Hibás lépésnél
feleződik a lépéshossz. A követő megtartja az induló ágat, nem vált át a másik
iránymezőre, és nem illeszt utólag simító görbét a pontokra.

Az ellenőrzés minden szakaszt feloszt az érintett bilineáris foltok határainál.
Egy folton belül az egyenes szakasz magassága másodfokú függvény. A szélsőérték
és a végponti deriváltak alapján a teljes szakasz magassági hibája és helyi
eséshibája ellenőrizhető; a végpontok megfelelő átlagos esése önmagában nem elég.
A diagram a folthatároknál és köztes pontokban újramintavételezett terepet mutatja.
A magassági tűrés numerikus követési tűrés, nem a bemeneti terep mérési pontossága.

## Korlátok és leállás

- Betöltött, méteres vízszintes koordinátájú terepmodell szükséges. Streamelt
  raszterből előbb kisebb területet kell kivágni. Földrajzi fokkoordinátákat és
  ismert nem méteres magassági egységet a felület elutasít. Hiányzó magassági
  egységnél métert feltételez; ezt a panel jelzi. Az egység nélküli EOV vízszintes
  koordinátáit méterként értelmezi.
- Mind a négy interpolációs saroknak érvényesnek kell lennie. Nincs NoData-áthidalás
  vagy a cellaközéppontok tartományán túli extrapoláció.
- Az előírt esésnél kisebb gradiensnél megáll. Sík területen 0%-hoz sem választ
  önkényes kezdőirányt. A 0%-os követés nem sík területen szintvonalkövetés.
- A terep esésével azonos előírt esésnél a két ág egybeeshet.
- Tűréshatár vagy az ágankénti 50 000 lépéses erőforráskorlát miatt rövidebb vonal
  is keletkezhet. A leállási ok külön megjelenik; ez nem bizonyítja, hogy más
  útvonalon nincs megoldás.
- A számítás megszakítható. Új kattintás megszakítja a folyamatban lévő keresést.
- Ez kezdőpontból induló nyomozás; két rögzített végpont összekötése és közúti
  ívsugár-korlátok érvényesítése még nem része.

## Ellenőrzés

Az automatizált tesztek analitikus síklejtőn ellenőrzik a két ág végpontját,
a pozitív, negatív és nulla esést, az elforgatott/nem négyzetes cellákat,
a görbült felület sűrű független mintavételét, a NoData-korlátot, a megszakítást
és a lépésszámkorlátot. A felületi integrációs teszt a két réteget és a célprofilt,
a projektből újraszámolt koordinátákat és az ERV export visszaolvasását is vizsgálja.

A natív macOS felületen kipróbált esetek: 12,5%-os analitikus síklejtőn +5%-os,
30 m-es kétágú vonal; a repó `P_00_01.ers` mintáján +0,2%-os, 10 000 m-es ágak.
Az utóbbi első ágának kijelzett legnagyobb hibája 0,0014 m és 0,0028 százalékpont
volt (kezdőpont közelítőleg E=532263,785; N=229342,899).
