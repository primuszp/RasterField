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
- Folytonos Gauss-simítás és a helyi dombormű trendje: a hiányzó cellák
  kimaradnak a normalizált súlyozásból. A sugár háromszorosa a szigmának.
- Sobel: emelkedés cellánként; vízszintes síkon nulla, egyenletes lejtőn
  nem nulla. Nem fokban vagy méter/méterben számított lejtés.
- Laplace: az alkalmazott előjel szerint pozitív a domború, negatív a
  homorú rész. A helyi dombormű a bemenet és a simított trend különbsége.
- A szélső cellák ismétlése a deriváltaknál és trendlevonásnál peremsávot
  okozhat. Ez nem valódi tereptörés; elemzéskor a szűrő félablakának
  megfelelő peremsávot külön kell kezelni.
- Nem véges mátrixsúly, eltolás, illetve nulla/nem véges osztó elutasítva.
  Az előbeállítások szöveges mátrixa teljes double pontossággal kerül átadásra.

## Ellenőrzés – 2026-10-25

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
