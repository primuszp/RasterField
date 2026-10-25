# Pixelszűrők – DigiTerra-referencia és ellenőrzés

## Forrás és megvalósítás

DigiTerra Map referencia-kézikönyv, C.IV.5.3, a PDF 65–66. oldala:
[archivált eredeti kézikönyv](https://web.archive.org/web/20220303185307id_/https://digiterra.hu/wp-content/static/dok/DTMAP_Referencia_kezikonyv.pdf).
Az eredeti gyártói URL az ellenőrzéskor 404 hibát adott.

A dokumentált lineáris művelet: a környezet súlyozott összege, osztva az
osztóval, majd hozzáadva az eltolást. A sor- és oszlopszám külön-külön
páratlan lehet. RasterFieldben az egyéni mátrix mindkét mérete 1–15 között
adható meg; négyzetes alak nem kötelező. A súlyok törtként is megadhatók.

A kézikönyv ábrájáról átvett „Simítás — DigiTerra Map 5×5” előbeállítás:

```text
1  2  3  2  1
2  7 11  7  2
3 11 17 11  3
2  7 11  7  2
1  2  3  2  1
```

Osztó: 121, eltolás: 0. A dokumentum ezt Gauss, Szórás=0.625 néven mutatja.
Az egész súlyokat közvetlenül alkalmazzuk, nem egy másként paraméterezett
folytonos Gauss-függvényből közelítjük. Az előbeállítás egyéni mátrixra
váltva szerkeszthető. A számítás a közös konvolúciós kódot használja.

A kézikönyv adaptív szűrőket is felsorol, de azok teljes képletét nem közli.
Ezek DigiTerra-azonos megvalósítását és a filter.dat importját ez a változás
nem tartalmazza. A szél- és NoData-kezelés azonossága sem bizonyított.

## RasterField számítási szabályai

- Lineáris mátrixok: a raszteren kívüli koordináták a legközelebbi szélső
  cellára korlátozódnak. Hiányzó szomszéd helyett alapból a középérték
  szerepel; szigorú módban a nem nulla súlyú hiányzó szomszéd NoData-t ad.
  A hiányzó középpont mindig hiányzó marad.
- Folytonos Gauss-simítás: a hiányzó cellák kimaradnak a normalizált
  súlyozásból. A sugár háromszorosa a szigmának.
- Helyi dombormű: Gauss-súlyozott helyi egyenesillesztés két egymás utáni,
  vízszintes és függőleges menetben. A középpontra illesztett trendet vonja
  le a felszínből. Teljes, szimmetrikus és hiánymentes ablakban ez a korábbi
  Gauss-átlag; csonka ablaknál a lejtésből eredő eltolódást is korrigálja.
  A hiányzó középpont hiányzó marad, a hiányzó szomszédok kimaradnak.
- Sobel: emelkedés cellánként; vízszintes síkon nulla, egyenletes lejtőn
  nem nulla. Nem fokban vagy méter/méterben számított lejtés.
- Laplace: az alkalmazott előjel szerint pozitív a domború, negatív a
  homorú rész. A helyi dombormű a bemenet és a simított trend különbsége.
- A Laplace-előbeállítás a határon másodfokú folytatást használ a három
  legközelebbi belső mintából (3, −3, 1). A sarkokban mindkét tengelyen
  alkalmazza. Kétcellás tengelyen lineáris, egycelláson konstans a folytatás.
  Szigorú NoData-módban a szükséges folytatási minták hiánya is NoData-t ad.
  A belső cellák számítása az eredeti 4/8 szomszédos mátrixszal azonos.
- Az egyéni mátrixok és a többi lineáris előbeállítás továbbra is a
  legközelebbi szélső cellát használják. Az egyéni Laplace-mátrix ezért
  a határon eltérhet a peremjavított Laplace-előbeállítástól.
- Nem véges mátrixsúly, eltolás, illetve nulla/nem véges osztó elutasítva.
  Az előbeállítások szöveges mátrixa teljes double pontossággal kerül átadásra.

## Első ellenőrzés – 2026-10-25, a peremjavítás előtt

A futó macOS alkalmazásban a P_00_01 640×450 cellás raszteren, külön-külön
az eredeti forrásból létrehozott rétegekkel:

| Szűrő | Beállítás | Megfigyelt minimum…maximum |
|---|---|---|
| Eredeti | – | 300.3…705.3 |
| Gauss | sugár 10 | 301.8…704.2 |
| Átlag | 5×5 | 300.8…705.1 |
| Élesítés | 5×5 | 299.9…705.5 |
| Sobel | alapbeállítás | 0.000723…3.749 |
| Laplace | átlós szomszédokkal | −4.149…3.704 |
| Helyi dombormű | sugár 10 | −3.236…3.044 |
| DigiTerra-mátrix | egyéniként és előbeállításként | 300.6…705.2 |

A simítás csökkentette, az élesítés kissé növelte a szélsőértékeket.
A Sobel a meredekebb részeket, a helyi dombormű a trendtől való eltérést
mutatta. A Laplace és a helyi dombormű peremsávja látható volt.
A sima mintaterepen a kisablakos simítás és élesítés hatása finom.

A nyolcréteges próba projektként mentve és újranyitva ugyanazokat az
értékeket adta. A réteg kikapcsolása az alatta levőt mutatta; az új
DigiTerra-réteg átrendezése és parancsból visszavonása megőrizte a nézetet.
Az átlátszatlanság módosítása a tulajdonságpanelre is átkerült.

A gyakorlati próba feltárt egy újraszámolási hibát: más típusú szűrőre
váltáskor a korábbi értéktartomány megmaradt, és fekete kép keletkezhetett.
A javítás ilyenkor újraszámolja a megjelenítést, valamint frissíti az
automatikus rétegnevet. Az egyedileg átnevezett réteget megőrzi.
Azonos típusú, azonos értékjelentésű paraméterváltozás megőrzi a kézi stílust.

Automatikus tesztek: pontos impulzusválasz, konstansok, téglalapos irányok,
osztó/eltolás, hibás bemenet, Gauss független kétdimenziós referencia alapján
NoData-val; továbbá mind a nyolc nem domborító szűrő réteglétrehozása,
láthatósága, átlátszatlansága, sorrendje, undo/redo és projekt-visszatöltése.
Külön regressziós teszt ellenőrzi a szűrőváltás utáni nevet és értéktartományt.


## Peremhatás javítása és ismételt ellenőrzés – 2026-10-25

A javítás a számított trendet és a perem menti deriváltat korrigálja;
nincs levágott vagy átlátszó peremsáv, a raszter mérete és illesztése megmarad.
A Laplace a belső cellákon bitazonos a korábbi mátrixos számítással.
A helyi dombormű a teljes szimmetrikus ablakokban a korábbi Gauss-trendet
adja, double köztes értékekkel; a szél és a NoData környezetében változik.

Ismételt próba ugyanazon P_00_01 modellen, helyi dombormű sugara 10 cella:

| Mérés a legkülső sor/oszlop celláin, a sarkokat egyszer számítva | Korábban | Javítva |
|---|---:|---:|
| Laplace: abszolút érték átlaga | 1.397985 | 0.059203 |
| Laplace: legnagyobb abszolút érték | 4.148804 | 0.205933 |
| Helyi dombormű: abszolút érték átlaga | 1.094034 | 0.038152 |
| Helyi dombormű: legnagyobb abszolút érték | 3.235809 | 0.155894 |

Ez 95.8%, illetve 96.5% csökkenés a külső cellák átlagos abszolút értékében;
ezek a mintaterep megfigyelései, nem általános hibabecslések.
A futó macOS alkalmazásban az eredeti projekt receptjeinek újraszámolása után,
az eredeti megjelenítési tartományokkal is eltűnt a keretszerű sáv.
A terepformák a szélig folytatódnak. A két réteg kapcsolható és külön menthető.

Mentett ellenőrzési fájlok a RasterField-ellenorzes-20261025 mappában:
`Laplace_peremjavitas.ers`, `Helyi_dombormu_r10_peremjavitas.ers`,
`Peremjavitas_ellenorzes.rfproj`, valamint a két `*-perem-javitva.png` képernyőkép.

343 sikeres teszt, köztük teljes lejtős sík minden szélen és sarokban,
1/2/3 cellás tengelyek, másodfokú görbület megtartása, valódi széli árok
megőrzése, NoData és szigorú mód, valamint a belső cellák összevetése a
korábbi képlettel. A projekt-visszatöltés tesztjei is az új számítást használják.

A polinomiális peremkezelés általános elvéhez lásd a
[SciPy Savitzky–Golay dokumentáció interp módját](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.savgol_filter.html).
A RasterField itt saját másodfokú peremfolytatást és Gauss-súlyozott helyi
lineáris trendet használ; ez nem Savitzky–Golay-implementáció.
