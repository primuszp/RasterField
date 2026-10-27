# Két pont közötti semleges vonal

Az **Elemzés → Semleges vonal…** panelen válaszd a **Két pont között** módot.
Add meg az előjeles esést, a maximális vonalhosszt, a magassági és helyi eséstűrést,
valamint a keresési állapotok korlátját. A **Kezdőpont kijelölése** után kattints
az A kezdőpontra, majd a B végpontra. Az A és B jelölők a megadott helyeket mutatják;
csak a teljesen ellenőrzött eredményből készül új vektorréteg.

A pozitív százalék A-tól B felé emelkedést, a negatív lejtést jelent. A hossz a
vízszintes vetület halmozott hossza. Betöltött, méteres terepmodell szükséges,
a kezdőpontból induló követővel azonos interpolációs és NoData-szabályokkal.

Az eredmény státusza, hossza, legnagyobb magassági és helyi eséshibája a panelen
látható. Az **Ablak…** az ellenőrző hossz-szelvényt nagyítható ablakban mutatja;
a **CSV…** a tényleges és az előírt magasságot együtt exportálja. A kész kapcsolat
ERV-ként menthető. A projekt tárolja a kezdő- és végpontot, a sávot és a keresési
paramétereket; újranyitáskor ezekből újraszámolja a vonalat.

## A végpontok geometriai feltétele

A célprofil `z(s) = zA + g*s`, ahol `g = esés / 100`.
Nem nulla esésnél a végpontmagasságokból számított névleges hossz:

`L = (zB - zA) / g`.

A panel ezt a hosszt is kiírja. Például 0,5 m szintkülönbség 5%-kal 10 m
vízszintes vonalhosszat kíván. Ha a két végpont légvonalban csak 4 m-re van,
egy megtört vonal teljesítheti ezt. A kereső ilyen irányváltoztatást is vizsgál.
Ha a szükséges hossz a légvonali távolságnál kisebb, a pontos modellben nincs
kapcsolat. A megvalósítás a megadott magassági tűrést is figyelembe veszi az előzetes
ellenőrzésben: nem nulla esésnél a kompatibilis hosszintervallum fél szélessége
`magassági tűrés / |g|`. A teljes szakasz helyi eséstűrését ettől függetlenül vizsgálja.

0%-nál nincs a magasságkülönbségből számítható névleges hossz. A végpontoknak a
magassági tűrésen belül azonos magasságúaknak kell lenniük, a keresést pedig a
megadott maximális vonalhossz korlátozza. Sík terepen is lehet kétpontos kapcsolat,
mert a végpont irányt ad a kereséshez.

## Keresési módszer

A Core belépési pontja a `NeutralLineTracer.FindRoute`, a beállításokat a
`NeutralRouteOptions`, az eredményt és a státuszt a `NeutralRouteResult` tartalmazza.
A korábbi kétágú `Trace` változat továbbra is használható.

1. Szigorú bilineáris mintavétellel ellenőrzi a végpontokat és a hosszak
   összeférhetőségét. Egyező végpont, adathiány, magassági összeférhetetlenség vagy
   túl kicsi hosszkorlát külön státuszt kap.
2. Megpróbálja a közvetlen kapcsolatot, teljes szakaszellenőrzéssel.
3. Kétszakaszos kapcsolathoz a lehetséges töréspontot egy ellipszisen keresi.
   A fókuszok az aktuális pont és a B végpont; a két szakasz összege a hátralévő
   névleges hossz. Az ellipszis és az előírt terepi profil metszését mintavétellel
   és gyökpontosítással keresi. Minden jelöltnél mindkét teljes szakaszt ellenőrzi.
   0%-nál több, a légvonalhoz képest hosszabb ellipszist próbál.
4. Ha ez kevés, prioritásos keresést végez. Az állapot világkoordinátát, megtett
   hosszt, érkezési irányt és előzményt tárol. A sor a megtett hossz, a céltávolság
   és egy irányváltoztatási költség szerint rendez. Több lehetséges irányt tart meg;
   kétpontos keresésben élesebb forduló is megengedett.
5. A szomszédjelölteket a meglévő gradiensirányok, cellahatár-metszések, helyi
   korrekció és körmetszéskeresés adja. Két külön lépési sugárral dolgozik.
   A távolságkorlátot meghaladó vagy a célig már el nem érő állapotokat elveti.
6. A B pont környezetében újra megpróbálja a közvetlen és az ellipszises zárást.
   A B végpont eredeti koordinátájához csatlakozik. Minden csatlakozó szakasz
   ugyanazon magassági, helyi esési és NoData-ellenőrzésen megy át.

A szakaszellenőrző a bilineáris foltok határainál felosztja a vonalat. Egy folton
belül a magassági eltérés másodfokú, a helyi esés lineáris függvény. A magasság
szélsőértékét és az esés végponti értékeit is ellenőrzi, így a megfelelő átlagos
esés önmagában nem elég az elfogadáshoz.

## Korlátok és értelmezés

A keresés véges felbontású. A hasonló állapotokat 1/8 pixeles hely-, 1/8
lépésléptékű hossz- és 32 részes irányosztással csoportosítja; 0%-nál a hossz
helyett az olcsóbb elérést tartja meg. Ez a keresési állapotok összevonását érinti,
az eredmény pontjait nem kerekíti cellaközéppontokra.

Az alapértelmezett bővítési korlát 25 000 állapot, a külön tárolási korlát 100 000
állapot. A panelen a bővítési keret állítható; a számítás megszakítható.
A keresési korlát elérése és a jelöltkeresés kimerülése külön státusz. Egyik sem
bizonyítja, hogy nincs más felbontással vagy más jelöltekkel megtalálható kapcsolat.
A módszer nem igazolja a globális optimumot.

A kereső töréspontokkal dolgozik; közúti legkisebb ívsugár és átmeneti ív nem része
az elfogadási feltételnek. Bármilyen későbbi simítás után újra kell ellenőrizni a
terepi hossz-szelvényt. A magassági tűrés numerikus követési követelmény, nem a
bemeneti terep mérési pontossága.

Sikertelen keresés nem hoz létre részleges eredményréteget vagy exportálható
félkész profilt. A korábban már létrehozott eredményrétegek a réteglistában maradnak.

## Ellenőrzés

A teljes tesztcsomag 368 sikeres tesztből áll. A kétpontos esetek ellenőrzik:

- az analitikus síklejtőn számítható egyenes és megtört kapcsolatot, ±5%-kal;
- a pontos kezdő- és végpontot, a névleges hosszt és a teljes terepi profilt;
- a 0%-os kapcsolat adathiányt megkerülő változatát;
- a több keresési állapotot igénylő görbült terepet;
- az elforgatott, nem négyzetes cellák világkoordinátáit;
- a hibás esésirányt, a hosszkorlátot, az egyező végpontokat és az adathiányt;
- a keresési korlátot, a megszakítást és a részleges eredmény kizárását;
- a felületi A/B-kijelölést, a két profilgörbét, a projektből való újraszámítást
  és az ERV-export visszaolvasását.

A görbült tesztraszter `z=0.1*x+0.001*x*y`, cellamérete 1 m. A kezdőpont
`(40.2,50.3)`, a végpont a kétágú követő első ágának 8 m-es pontja. A magassági
tűrés 0,0001 m, a helyi eséstűrés 0,02 százalékpont. A külön összehasonlító
futtatás 234 keresési állapottal talált kapcsolatot.

Natív macOS próba: 12,5%-os síklejtő, +5%-os keresés. Két térképi kattintás után
az A≈(41.2124233,51.5083154) és B≈(54.3282094,51.5083154) pontok között
32,7894654 m-es kapcsolat készült. A külön ablakos hossz-szelvényen a terepi és
előírt görbe egybeesett. A 91 mintás CSV-t visszaolvasva a két értékoszlop
legnagyobb különbsége 0 volt az exportált pontosságon. A hibás irányú +5%-os
kapcsolatot a natív felület is elutasította.

A repó `P_00_01.ers` mintáján is futott számítás: a korábban ellenőrzött
A=(532263.7846912544,229342.8991133728) pontból +0,2%-kal nyomozott első ág
1 km-es és 5 km-es végpontjára kerestünk kapcsolatot. Mindkettő sikeres volt.
Az 5 km-es eset 39 állapotot vizsgált; a kapott hossz 4999,9351 m, a legnagyobb
magassági hiba 0,019209 m, a helyi eséshiba 0,003271 százalékpont volt. A külön,
1 m-es közű, 5017 mintás Float32 profil 0,019227 m legnagyobb magassági eltérést
adott, szintén a 0,02 m-es tűrésen belül. Ez a próba Core-számítás és független
profil-mintavétel; a natív kétkattintásos próba az analitikus síklejtőn történt.
