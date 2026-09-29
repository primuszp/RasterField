# RasterField — UI / UX terv

> Mini‑GIS réteges megjelenítésre és elemzésre, elsősorban **ER Mapper `.ers` +
> BIL** raszterekhez; vektorok (`.erv`) megjelenítése és **raszterből levezetett
> vektorok** (szintvonalak, vízfolyások, körvonalak); **Bézier‑patch
> interpoláció** a felbontás subdivision‑nel történő növeléséhez.

Ez a dokumentum a jelenlegi alkalmazásra épít (Avalonia, `MainWindow`,
`RasterView`, `RasterLayer`, `VectorLayer`, `RasterField.Core`), és
megmondja, mi marad, mi változik, mi új.

---

## 1. Célok és alapelvek

| # | Cél | Mérhető elvárás |
|---|-----|-----------------|
| C1 | Egy `.ers`/BIL adat **5 mp‑en belül** értelmezhető képként jelenjen meg | Megnyitáskor automatikus paletta + 2–98 % stretch + jelmagyarázat |
| C2 | A **réteg** a központi fogalom — minden eszköz az *aktív* rétegre hat | Az aktív réteg mindig látható: kiemelt kártya + a státuszsorban a neve |
| C3 | Minden elemzés **eredménye új réteg**, nem felugró ablak vagy elvesző fájl | Származtatott réteg forrás‑hivatkozással („lineage”), mentés opcionális |
| C4 | Nagy adat (> 16 M cella) **ugyanúgy** kezelhető, mint a kicsi | Streaming, előnézet csak a látható ablakra, hosszú műveletek háttérben, megszakíthatóan |
| C5 | Nem romboló munkafolyamat | Az eredeti fájl sosem íródik felül mentés nélkül; visszavonás (Undo) réteg‑ és beállítás‑szinten |
| C6 | Magyar és angol felület | Minden felirat erőforrásból, `hu`/`en` |

**UX alapelvek**

1. **Réteg → Eszköz → Eredményréteg.** Minden művelet ugyanazt a mintát követi.
2. **Előnézet, mielőtt számolna.** Szintvonal, interpoláció, hillshade: élő
   előnézet a látható kivágaton, a teljes futtatás csak „Alkalmaz”‑ra.
3. **Progresszív felfedés.** Alapból egyszerű panel; a haladó paraméterek
   összecsukható „Haladó” szekcióban.
4. **Mindig tudd, hol vagy.** Koordináta, cellaindex, érték, lépték, vetület
   (EPSG) folyamatosan látható.
5. **Egy kattintásnyira a kontextus.** Réteg jobb‑klikk menüje tartalmaz minden,
   arra a rétegre értelmes műveletet (a főmenü ugyanezeket csoportosítja).

---

## 2. Felhasználói szerepek (persona)

| Persona | Tipikus feladat | Amit a UI‑nak adnia kell |
|---|---|---|
| **Terepi geológus / geofizikus** | Mágneses/gravitációs rács (`.ers`) átnézése, anomáliák, profil | Gyors paletta‑váltás, hisztogram‑stretch, profil, pont‑azonosítás |
| **Térképész / DTM‑felhasználó** | Domborzat, szintvonal, árnyékolás, export | Szintvonal‑generálás címkékkel, hillshade overlay átlátszósággal, PNG / `.erv` export |
| **Hidrológus** | Lefolyás, vízgyűjtő | Flow acc. → vízhálózat vektorként, küszöb élő csúszkával |
| **Adatfeldolgozó** | Konvertálás, kivágás, mozaik, felbontás‑növelés | Kötegelt, megismételhető műveletek, pontos metaadat‑nézet |

---

## 3. Főablak elrendezés

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ Fájl  Nézet  Réteg  Raszter  Vektor  Domborzat  Interpoláció  Eszközök  Súgó        │ ← menü
├──────────────────────────────────────────────────────────────────────────────────────┤
│ [📂][➕][💾] │ [✋][🔍+][🔍−][⤢] │ [ⓘ][📏][📐][✂][〰 profil] │ [≋ szintvonal][◇ Bézier] │ 🔎 parancs… │ ← eszköztár
├───────────────┬──────────────────────────────────────────────────────┬───────────────┤
│ RÉTEGEK    ⚙  │                                                      │ TULAJDONSÁG   │
│ ┌───────────┐ │                                                      │ ┌───────────┐ │
│ │▣ DTM      │ │                                                      │ │ Megjelenít│ │
│ │ 👁 ▮▮▮▮▯ 80%│ │                 TÉRKÉPVÁSZON                         │ │ Paletta ▾ │ │
│ │ └ ≋ Szint.│ │                                                      │ │ Stretch ▾ │ │
│ │ └ ☼ Hillsh│ │        (raszterek + vektorok, közös vetületben)       │ │ Gamma ──● │ │
│ │▤ Utak.erv │ │                                                      │ │ ▸ Haladó  │ │
│ │▣ Mágneses │ │                                                ┌────┐│ ├───────────┤ │
│ └───────────┘ │                                                │ ▦  ││ │ Hisztogram│ │
│ [+ réteg] [📁]│  ┌────────────┐                                │jelm.││ │ ▁▃▇█▅▂▁  │ │
├───────────────┤  │ ⊕ áttekintő│                      0   500 m └────┘│ │ min ◂──▸ max│
│ ELEMZÉS    ▾  │  └────────────┘                      ┕━━━━━┙          │ ├───────────┤ │
│ Statisztika   │                                                      │ │ Metaadat  │ │
│ Pontazonosító │                                                      │ │ (ERS fej) │ │
│ Profil        │                                                      │ └───────────┘ │
├───────────────┴──────────────────────────────────────────────────────┴───────────────┤
│ E 652 310.5  N 241 880.2 │ cella [312, 188] │ érték 214.37 m │ 1:12 400 │ EOV EPSG:23700 │ ▣ DTM │ ⏳ 2 feladat │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

**Régiók**

| Régió | Tartalom | Viselkedés |
|---|---|---|
| **Bal dokk — Rétegek** | Réteg‑fa (raszter ▣, vektor ▤, származtatott ↳), csoportok | Húzással rendezés, 👁 láthatóság, átlátszóság‑csúszka inline, jobb‑klikk menü |
| **Bal dokk — Elemzés** | Statisztika, Pontazonosító, Profil, Mérés eredménylisták | Fülek; a Profil diagram itt dokkolható (ma külön ablak) |
| **Középen — Vászon** | Réteges render, áttekintő térkép (minimap), léptékvonal, É‑nyíl, jelmagyarázat | Pan/zoom, eszközmódok, lebegő eszköz‑sávok (mint a mai Clip sáv) |
| **Jobb dokk — Tulajdonság** | Az *aktív réteg* beállításai + hisztogram + metaadat | A réteg típusától függően más szekciók (7. fejezet) |
| **Státuszsor** | Koordináta, cella, érték, lépték, vetület, aktív réteg, háttérfeladatok | Kattintás a koordinátán → „Ugrás koordinátára” |

- A dokkok összecsukhatók (`F9` bal, `F10` jobb, `F11` csak vászon).
- **Parancskereső** (`Ctrl+K`): minden menüpont gépeléssel elérhető (pl. „szint” → *Szintvonal generálás…*).
- Az elrendezés (dokk‑szélességek, nyitott fülek) a meglévő `AppSettings`‑be mentődik.

---

## 4. Rétegmodell

### 4.1 Réteg típusok

| Ikon | Típus | Forrás | Megjegyzés |
|---|---|---|---|
| ▣ | **Raszter** | `.ers` + BIL (bármely `CellType`, több sáv) | Streaming, ha nagy |
| ▣▣▣ | **RGB kompozit** | 3+ sávos `.ers` | Sáv → R/G/B hozzárendelés a panelen |
| ▤ | **Vektor** | `.erv` (+ később GeoJSON / CSV pontok) | Stílus rétegenként és attribútum szerint |
| ↳▣ | **Származtatott raszter** | Slope, Aspect, Hillshade, Band math, **Bézier‑subdivision**, … | Memóriában él, amíg nincs mentve („●” jelzés a néven) |
| ↳▤ | **Származtatott vektor** | Szintvonal, vízhálózat, körvonal, profil‑vonal | Szerkeszthető stílus, `.erv` exportálható |
| 🗀 | **Csoport** | Felhasználói | Közös láthatóság/átlátszóság |

### 4.2 Réteg kártya (bal panel)

```
┌──────────────────────────────────────────┐
│ 👁 ▣ DTM_2024 ●                  ⋮      │  ● = mentetlen,  ⋮ = kontextus menü
│    Elevation ▸ 2–98 %   ▮▮▮▮▮▮▮▯ 80 %    │  mini paletta‑csík + átlátszóság
│    ├ 👁 ↳≋ Szintvonal 10 m               │  származtatott gyerekrétegek
│    └ 👁 ↳☼ Hillshade (multiply)          │  keverési mód jelölve
└──────────────────────────────────────────┘
```

- **Aktív réteg**: akcentus‑keret (ahogy ma), + neve a státuszsorban.
- **Származtatott rétegek a forrás alá húzva** jelennek meg (behúzva); a sorrend
  ettől még szabadon módosítható. Ha a forrás törlődik → figyelmeztetés:
  „A 2 származtatott réteg forrás nélkül marad. Megtartod őket?”
- **Keverési mód** rasztereknél: Normál / Szorzás (hillshade‑hez) / Képernyő / Overlay.
- **Nagy adat** jelzés: `⇶ streaming` címke a kártyán.

### 4.3 Kontextus menü (⋮ vagy jobb‑klikk)

Raszter: *Nagyítás a rétegre · Tulajdonságok · Statisztika · Hisztogram ·
Szintvonal… · Hillshade · Slope/Aspect · Bézier‑subdivision… · Kivágás ·
Duplikálás · Mentés másként… · Forrás megnyitása mappában · Eltávolítás*

Vektor: *Nagyítás · Stílus · Attribútum tábla · Címkék · Szűrés · Mentés
`.erv`‑ként · Eltávolítás*

---

## 5. Munkafolyamatok (user flow)

### 5.1 Megnyitás és első kép

```
Húzd be / Fájl ▸ Megnyitás  →  csak fejléc (LoadHeaderOnly)  →  Méret? ──nagy──▸ streaming overview
                                                                   └─kicsi──▸ teljes betöltés
      → automatikus: paletta (utoljára használt vagy adat‑típus szerinti), 2–98 % stretch
      → jelmagyarázat + nagyítás a teljes kiterjedésre
      → toast: „DTM_2024 · 640×450 · IEEE4 · EOV (EPSG:23700) · 112–418 m”
```

- Első rétegnél a vászon üres‑állapota: nagy drop‑zóna „Húzz ide `.ers` vagy `.erv` fájlt” + Legutóbbi fájlok listája.
- Vektor első rétegként: engedélyezett, a vászon a vektor kiterjedéséből veszi a koordináta‑keretet (ma raszter kell hozzá — ezt feloldjuk).

### 5.2 Elemzés

1. Réteg kiválasztása → jobb panel: **Hisztogram** interaktív (húzható min/max fogantyúk = stretch).
2. **Pontazonosító (`I`)**: kattintásra lista *minden* látható réteg értékével az adott pontban (raszter: érték/sávok; vektor: attribútum).
3. **Profil (`P`)**: több töréspontú vonal (ma kétpontos) → dokkolt diagram, több raszter egyszerre (pl. eredeti vs. Bézier‑interpolált).
4. **Mérés (`M`)**: távolság / terület; a raszteren a vonal menti 3D felszíni hossz is.
5. **Statisztika**: aktív rétegre, kijelölt területre (téglalap/sokszög) vagy vektor‑sokszögön belül (zonális statisztika).

### 5.3 Származtatás

```
Réteg ▸ Szintvonal…  →  dialógus élő előnézettel  →  [Alkalmaz]  →  új ↳▤ réteg a forrás alatt
                                                    →  [Mentés .erv‑ként] opcionális
```

Minden elemző dialógus azonos vázra épül (lásd 8. fejezet).

---

## 6. Vászon és interakció

| Eszköz | Gyorsbill. | Kurzor | Lebegő sáv |
|---|---|---|---|
| Pan | `Space` (nyomva) / `H` | ✋ | — |
| Zoom ablak | `Z` | 🔍 | — |
| Pontazonosító | `I` | ⓘ | eredmény a bal panelen |
| Profil | `P` | + | „Pont hozzáadása · Kész (Enter) · Mégse (Esc)” |
| Mérés | `M` | 📏 | futó összeg |
| Kivágás | `C` | ✂ | „Méret px / m · Kivág és ment · Mégse” (meglévő) |
| Bézier‑terület | `B` | ◇ | „Terület: 800×600 → 3200×2400 (×4) · Előnézet · Alkalmaz” |

- Egér: görgő = zoom a kurzorhoz, középső gomb = pan mindig, bármely eszközben.
- Nagyítás 1:1 fölött a cellarács (`G`) automatikusan felajánlható; a
  **megjelenítési simítás** választó: *Legközelebbi / Bilineáris / Bézier (bikubikus)* (lásd 9.4).
- **Áttekintő térkép** (bal alsó, összecsukható) a teljes kiterjedéssel és a nézet‑téglalappal.
- **Léptékvonal + É‑nyíl + jelmagyarázat** a vászonon, PNG exportba is bekerülnek (kapcsolható).
- **Könyvjelzők** (`Ctrl+1..9`): nézet mentése/visszaugrás.

---

## 7. Jobb panel — Tulajdonságok

### 7.1 Raszter

```
┌ MEGJELENÍTÉS ────────────────────────┐
│ Mód     (•) Egysávos  ( ) RGB        │
│ Sáv     [ 1 — Elevation      ▾]      │
│ Paletta [██████ Elevation    ▾] ⇄ ✎ │   ⇄ fordít, ✎ paletta‑szerkesztő (meglévő)
│ Mód     [Folytonos ▾]  Osztályok [10]│
│ Stretch [2–98 % ▾]                   │
│ Gamma   ─────●──── 1.00              │
│ Átlátsz.────────●─ 80 %  Keverés[▾]  │
│ No‑data [■ átlátszó ▾]               │
│ ▸ Haladó: újramintavétel, cellarács  │
├ HISZTOGRAM ──────────────────────────┤
│ ▁▂▃▅▇██▇▅▃▂▁                          │
│ ◆──────────────────◆   min 112 max 418│   fogantyúk húzhatók → Manual stretch
│ [lin | log]   μ 241.3  σ 48.2         │
├ INFORMÁCIÓ ──────────────────────────┤
│ 640 × 450 · 1 sáv · IEEE4 · LSBFirst │
│ Cella 50 × 50 m · Forgatás 0°        │
│ EOV · EPSG:23700                     │
│ No‑data: 12 cella (0 a burkon belül) │
│ [Teljes ERS fejléc megtekintése…]    │   ErsHeader fa‑nézet, csak olvasható
└──────────────────────────────────────┘
```

### 7.2 Vektor

- **Stílus**: szín, vonalvastagság, kitöltés + átlátszóság, pontszimbólum és méret.
- **Osztályozás attribútum szerint**: egyedi értékek vagy tartományok (szintvonalnál a *szint* mező → paletta szerinti színezés).
- **Címkék**: mező, betűméret, vonal menti elhelyezés, halo; ütközés‑elkerülés.
- **Láthatósági lépték** tartomány (pl. címke csak 1:25 000 alatt).

### 7.3 Származtatott réteg — extra szekció

```
┌ LEVEZETÉS ───────────────────────────┐
│ Forrás:   DTM_2024 (sáv 1)           │
│ Művelet:  Szintvonal                 │
│ Param.:   köz 10 m, fő 50 m, simítás │
│ [Paraméterek módosítása…] [Újraszámol]│
│ ● Mentetlen   [Mentés…]              │
└──────────────────────────────────────┘
```

A levezetés **receptként** tárolódik (forrás + művelet + paraméterek), így
újraszámolható és a projekt‑fájlba menthető (lásd 11. fejezet).

---

## 8. Elemző dialógusok — közös váz

```
┌ <Művelet neve> ───────────────────────────────────────────── ✕ ┐
│ Forrás réteg  [ DTM_2024 ▾ ]   Sáv [1 ▾]                        │
│─────────────────────────────────────────────────────────────────│
│ Alap paraméterek (2–4 mező, értelmes alapértékekkel)            │
│ ▸ Haladó                                                        │
│─────────────────────────────────────────────────────────────────│
│ ☑ Élő előnézet (látható kivágat)       Becsült méret: 48 MB     │
│ Kimenet: (•) Új réteg  ( ) Mentés fájlba [..........] [📁]      │
│─────────────────────────────────────────────────────────────────│
│                               [Alapértékek] [Mégse] [Alkalmaz]  │
└─────────────────────────────────────────────────────────────────┘
```

- **Nem modális** (a vászon mozgatható, közben frissül az előnézet).
- Hibás érték → mező alatti piros üzenet, az „Alkalmaz” tiltva, indoklással.
- Hosszú futás → háttérfeladat a státuszsorban (⏳), **megszakítható**; kész
  esetén toast „Szintvonal kész — 1 284 vonal · [Megmutat]”.

---

## 9. Vektorok levezetése

### 9.1 Szintvonalak (`ContourGenerator` + bővítés)

```
┌ Szintvonal generálás ───────────────────────────────────────────┐
│ Forrás [DTM_2024 ▾]                                             │
│ Tartomány  min [100]  max [420]   (adatból: 112 – 418)          │
│ Alapköz    [10] m     Főszintvonal minden [5]. (= 50 m)         │
│ ▸ Haladó                                                        │
│   Forrás‑felszín  (•) Eredeti  ( ) Bézier ×2  ( ) Bézier ×4     │  ← 10. fejezet
│   Vonalsimítás    [Nincs | Chaikin 1× | 2× | 3×]                │
│   Min. hossz      [ 3 ] cella  (rövid zajvonalak elhagyása)     │
│   Zárt vonalak a szegélyen  ☐                                   │
│ Stílus: alap ─ 0.5 px barna, fő ━ 1.2 px barna, címke fő vonalon │
│ ☑ Élő előnézet        Várható: ~1 300 vonal                     │
└─────────────────────────────────────────────────── [Alkalmaz] ──┘
```

- Kimenet: ↳▤ réteg `level` és `index` (fő/alap) attribútummal, alapértelmezett
  stílussal és vonal menti címkékkel.
- Mentés `.erv`‑be a meglévő `VectorDataWriter`‑rel (a `level` az attribútumba kerül, ahogy ma).
- **Bézier‑felszínből** generált szintvonal láthatóan simább, lépcsőmentes —
  ez a két funkció legfontosabb szinergiája.

### 9.2 További levezetett vektorok (ütemezve)

| Levezetés | Alap | Kimenet |
|---|---|---|
| **Vízhálózat** | `HydrologyAnalysis.FlowAccumulation` + küszöb | Vonalak, Strahler‑rend attribútummal; küszöb élő csúszkával |
| **Láthatósági körvonal** | `ViewshedAnalysis` | Sokszög(ek) a látható területről |
| **Adat‑burok** | `ConvexHull` | Sokszög — az adat valós lefedettsége |
| **Osztály → sokszög** | Küszöbölt / osztályozott raszter | Sokszögek osztály attribútummal |
| **Profil‑vonal** | Profil eszköz | Vonal + mintavételi pontok attribútummal |
| **Kivágási keret** | Kivágás eszköz | `box` objektum |

### 9.3 Vektor formátumok

- **Most:** `.erv` olvasás/írás (meglévő).
- **Következő:** GeoJSON import/export (vetület‑jelölés nélkül, a projekt vetületét feltételezve, figyelmeztetéssel), CSV pont‑import (X, Y, [érték]).
- **Később:** Shapefile, DXF (szintvonal CAD‑be).

---

## 10. Bézier‑patch interpoláció és subdivision

### 10.1 Mit old meg

A rács‑adat (pl. 50 m‑es DTM) nagyításkor lépcsős/kockás, a bilineáris
interpoláció pedig törésvonalakat hagy a cellahatárokon. A **bikubikus
Bézier‑patch** minden cellára egy sima, a szomszédokkal **C¹ folytonosan**
illeszkedő felületdarabot illeszt; ezt `k`‑szoros felosztással
(subdivision) újramintavételezve nagyobb felbontású raszter jön létre,
amely simább megjelenítést, szintvonalat, árnyékolást és profilt ad.

> Az interpoláció **nem teremt új információt** — ezt a UI is kimondja
> (info‑sor a dialógusban), hogy a felhasználó ne tekintse mért adatnak.

### 10.2 Matematika (implementációs specifikáció)

A rács értékei a cellák **középpontjában** vannak (a `RasterGeoReference` /
`RasterProfiler.BilinearSample` konvenciója). Egy patch a négy szomszédos
cellaközéppont `(i,j)…(i+1,j+1)` által határolt négyzetet fedi le,
`u, v ∈ [0,1]`:

```
S(u,v) = Σ_{a=0..3} Σ_{b=0..3}  B_a(u) · B_b(v) · P_ab        B_a = Bernstein‑polinomok (köbös)
```

A 16 kontrollpont a 4×4‑es cellakörnyezetből:

| Kontrollpont | Érték |
|---|---|
| Sarkok `P00, P30, P03, P33` | a négy cellaérték `z` |
| Élek (pl. `P10`, `P20`) | `z ± Dx / 3` |
| Élek (pl. `P01`, `P02`) | `z ± Dy / 3` |
| Belső (csavarás) `P11…P22` | `z ± Dx/3 ± Dy/3 + Dxy/9` (előjel a saroktól függ) |

ahol a derivált‑becslések centrális differenciával (Catmull‑Rom):

```
Dx  = τ · (z[i+1,j] − z[i−1,j]) / 2
Dy  = τ · (z[i,j+1] − z[i,j−1]) / 2
Dxy = τ · (z[i+1,j+1] − z[i+1,j−1] − z[i−1,j+1] + z[i−1,j−1]) / 4
```

- **τ (feszesség)**: `1.0` = Catmull‑Rom (alap); `0` = a patch a bilineárisba
  degenerál (sarok‑értékek, lapos érintők). Csúszka 0–1 között.
- **Túllövés‑korlát (monoton mód)**: a Fritsch–Carlson‑elv szerint a
  deriváltakat úgy vágjuk, hogy a patch ne lépje túl a 4 sarok min/max‑át
  (hasznos éles peremeknél, pl. töltés, bányafal). Kapcsoló: *Túllövés
  megengedése / Monoton*.
- **Szegély**: a rácson kívüli szomszéd tükrözéssel (reflect) pótolva.
- **No‑data**: ha a 4×4 környezetben van no‑data → az adott patch
  *visszaesés* a (no‑data‑tűrő) bilineárisra; ha a 4 sarok közül is hiányzik
  → no‑data. Választható: *Visszaesés bilineárisra / No‑data* (a
  `NoDataFiller` előtte felajánlva: „12 lyuk van a burkon belül — előbb kitöltöd?”).
- **Subdivision**: `k ∈ {2, 3, 4, 8, egyedi}` felosztás → minden patch
  `k × k` új cella. Az új cellaméret `cellSize / k`; a **regisztráció
  újrahorgonyozva** úgy, hogy a kiterjedés és a forgatás változatlan marad
  (az `ErsDocument.Clip` meglévő újrahorgonyzási logikájával összhangban).
  Az új cellák középpontjai `u = (m + 0.5)/k` pontokban vannak kiértékelve.
- **Iteratív (de Casteljau) felosztás** nem szükséges: a közvetlen kiértékelés
  egyszerűbb és párhuzamosítható (soronként, mint a `RasterImageRenderer`).

### 10.3 Javasolt Core API

```csharp
namespace RasterField.Rasters;

public sealed class BezierPatchOptions
{
    public int Factor { get; init; } = 4;                        // k
    public double Tension { get; init; } = 1.0;                  // τ
    public bool Monotone { get; init; }                           // túllövés‑korlát
    public BezierNoDataMode NoData { get; init; } = BezierNoDataMode.FallbackBilinear;
}

public static class BezierPatchInterpolator
{
    // Teljes raszter k‑szoros felbontással.
    public static Raster Subdivide(Raster source, BezierPatchOptions options,
                                   IProgress<double>? progress = null, CancellationToken ct = default);

    // Egy pont kiértékelése (profil, pontazonosító, megjelenítés).
    public static float? Sample(Raster source, double pixelX, double pixelY, BezierPatchOptions options);

    // Csak egy ablak (streaming / élő előnézet): a forrás‑ablakot 1 cellás margóval kell átadni.
    public static Raster SubdivideWindow(Raster sourceWindow, int marginCells, BezierPatchOptions options);
}

// ErsDocument szinten, georeferencia‑újrahorgonyzással:
public ErsDocument Subdivide(BezierPatchOptions options /*, opcionális pixel‑ablak */);
```

**Tesztek** (`BezierPatchInterpolatorTests`):
sík felület pontosan reprodukálódik (bármely τ‑ra) · a sarokpontokon az
eredeti érték jön vissza · szomszédos patchek határán érték és derivált
folytonos · monoton módban nincs túllövés lépcsőfüggvényen · no‑data
viselkedés mindkét módban · georeferencia: az új raszter `WorldBounds`‑a
megegyezik az eredetivel · `k = 1` → identitás.

### 10.4 Három belépési pont a UI‑ban

**A) Megjelenítési simítás (nem módosít adatot)**
*Nézet ▸ Nagyítási simítás ▸ Legközelebbi / Bilineáris / Bézier*
— 1:1 fölötti nagyításnál a látható ablakot a `SubdivideWindow` számolja
a képernyő‑felbontáshoz illesztett `k`‑val. Streaming módban is működik.

**B) Subdivision eszköz (új raszter réteg)** — *Interpoláció ▸ Bézier‑subdivision…*

```
┌ Bézier‑patch subdivision ───────────────────────────────────────┐
│ Forrás [DTM_2024 ▾]  Sáv [1 ▾]                                  │
│ Felosztás  ( ) ×2  ( ) ×3  (•) ×4  ( ) ×8  ( ) egyedi [  ]      │
│            50 m → 12.5 m cella · 640×450 → 2560×1800            │
│ Terület    (•) Teljes réteg  ( ) Jelenlegi nézet  ( ) Rajzolt ▭ │
│ ▸ Haladó                                                        │
│   Feszesség τ   ─────────●  1.00   (0 = bilineáris)             │
│   Túllövés      (•) Megengedett  ( ) Monoton (peremekhez)       │
│   No‑data       (•) Bilineáris visszaesés  ( ) No‑data marad    │
│   Kimeneti típus [IEEE4 ▾]                                      │
│─────────────────────────────────────────────────────────────────│
│ ┌──── előnézet (osztott nézet) ────┐   Becsült méret: 18.4 MB   │
│ │ eredeti ▦▦▦ │◀▶│ ◇ Bézier ×4    │   ⚠ > 16 M cella esetén    │
│ └──────────────────────────────────┘     streaming rétegként jön│
│ ⓘ Az interpoláció simít, de nem ad új mért információt.         │
│                                         [Mégse] [Alkalmaz]      │
└─────────────────────────────────────────────────────────────────┘
```

- **Osztott előnézet** (húzható elválasztó) a látható kivágaton: bal oldal
  eredeti, jobb oldal interpolált — ugyanazzal a palettával/stretch‑csel.
- **Méretbecslés** élőben; nagy kimenetnél javaslat: „Csak a nézetre” vagy
  „Mentés közvetlenül fájlba (csempézve)”.
- Eredmény: `↳▣ DTM_2024 · Bézier ×4` réteg, örökli a forrás megjelenítési
  beállításait; a Levezetés szekcióban τ/k módosítható és újraszámolható.

**C) Más eszközök bemeneteként**
- Szintvonal: *Forrás‑felszín: Bézier ×k* (9.1).
- Profil: *Mintavétel: Bilineáris / Bézier* — mindkét görbe egy diagramon.
- Pontazonosító: a Bézier‑becsült érték is megjelenik (dőlt betűvel, „interpolált” jelöléssel).
- Hillshade / Slope: opcionálisan a subdivided felszínen.

### 10.5 Teljesítmény

- Soronkénti párhuzamosítás; a 4×4 környezet és a kontrollpontok cellánként
  egyszer számolódnak, a `k × k` kiértékelés előre kiszámolt Bernstein‑súlytáblával
  (`k` × 4 érték tengelyenként) → cellánként 16 szorzás‑összeadás.
- Nagy kimenet: csempés (pl. 512×512) feldolgozás 2 cellás átfedéssel,
  közvetlenül `BilRasterWriter`‑be, így a memória korlátos marad.

---

## 11. Projekt és állapot

- **Projektfájl** (`.rfproj`, JSON): rétegek sorrendje, útvonalai (relatív),
  megjelenítési beállításai, származtatott rétegek receptjei, könyvjelzők, nézet.
  Mentetlen származtatott réteg esetén megkérdezi: menti‑e az adatot is, vagy csak a receptet.
- **Visszavonás** (`Ctrl+Z / Ctrl+Y`): réteg hozzáadás/törlés/sorrend,
  megjelenítési beállítások, stílus. (Számítások nem „visszavonódnak”, hanem
  a réteg törölhető.)
- **Bezáráskor** figyelmeztetés mentetlen rétegekre, listával.

---

## 12. Menüszerkezet (javasolt)

| Menü | Tartalom |
|---|---|
| **Fájl** | Új projekt · Megnyitás… · Réteg hozzáadása… · Legutóbbiak · Projekt mentése · Mentés másként (réteg) · Exportálás ▸ PNG / `.erv` / GeoJSON / CSV · Kilépés |
| **Nézet** | Nagyítás rétegre / teljes · Nagyítási simítás ▸ · Cellarács · Áttekintő · Léptékvonal · Jelmagyarázat · Panelek ▸ · Téma ▸ · Nyelv ▸ |
| **Réteg** | Tulajdonságok · Duplikálás · Csoportosítás · Fel/Le · Eltávolítás · Nagyítás a rétegre |
| **Raszter** | Statisztika · Hisztogram · Sáv matematika… · No‑data kitöltés… · Kivágás ▸ · Mozaik… · Típus/bájtsorrend konverzió… |
| **Vektor** | Szintvonal… · Vízhálózat… · Osztály → sokszög… · Adat‑burok · Attribútum tábla |
| **Domborzat** | Lejtő · Kitettség · Árnyékolás · Görbület ▸ · Svájci stílusú domborzat · Lefolyás irány/akkumuláció · Láthatóság… |
| **Interpoláció** | Bézier‑subdivision… · Nagyítási simítás ▸ · Újramintavétel cellaméretre… |
| **Eszközök** | Pontazonosító · Profil · Mérés · Kivágás · Ugrás koordinátára… · Paletta‑szerkesztő |
| **Súgó** | Gyorsbillentyűk · ERS formátum‑leírás · Névjegy |

A meglévő *Tools* és *Terrain* menük tartalma a fenti **Raszter / Vektor /
Domborzat / Interpoláció** menükbe oszlik szét; macOS‑en ugyanez a
`NativeMenu`‑ben.

---

## 13. Gyorsbillentyűk

| Billentyű | Művelet | Billentyű | Művelet |
|---|---|---|---|
| `Ctrl+O` | Megnyitás | `Ctrl+Shift+O` | Réteg hozzáadása |
| `Ctrl+S` | Projekt mentése | `Ctrl+K` | Parancskereső |
| `0` / `F` | Teljes kiterjedés | `Ctrl+± / görgő` | Zoom |
| `H` / `Space` | Pan | `I` | Pontazonosító |
| `P` | Profil | `M` | Mérés |
| `C` | Kivágás | `B` | Bézier terület |
| `G` | Cellarács | `L` | Jelmagyarázat |
| `Alt+↑/↓` | Aktív réteg fel/le | `Ctrl+H` | Aktív réteg láthatóság |
| `Tab` | Következő réteg aktív | `Esc` | Eszköz megszakítása |
| `F9 / F10 / F11` | Bal / jobb dokk / csak vászon | `Ctrl+Z / Y` | Visszavonás / Újra |

---

## 14. Vizuális nyelv, akadálymentesség

- Meglévő `AppTheme` tokenek (világos/sötét) — új tokenek: `Derived` (származtatott
  réteg jelölés), `Warning`, `PreviewDivider`.
- Ikonok egységes vonalas készlet; a réteg‑típus ikon **és** szöveges címke együtt
  (nem csak szín különbözteti meg).
- Palettaválasztóban **színvak‑barát** jelölés (Viridis, Cividis ajánlott címkével).
- Minden vezérlő billentyűzettel elérhető, fókuszkeret látható; minimális
  kattintási cél 28 px (a mai ▲/▼/✕ körökhöz igazodva).
- Számformátum a nyelvhez igazodik (hu: `214,37`, szóköz ezres elválasztó),
  a fájlokba írt értékek mindig invariáns kultúrával.

---

## 15. Megvalósítási ütemterv

| Fázis | Tartalom | Érintett kód |
|---|---|---|
| **F1 — Alap átrendezés** | Dokkolt 3‑oszlopos elrendezés, jobb panel szekciók, interaktív hisztogram, metaadat‑nézet, parancskereső, HU/EN erőforrások | `MainWindow` szétbontása (`LayersPanel`, `PropertiesPanel`, `AnalysisPanel`), `AppSettings` |
| **F2 — Rétegmodell** | Származtatott rétegek (memóriában, ● jelzés, recept), csoportok, keverési mód, vektor első rétegként | `RasterLayer`, `VectorLayer`, új `DerivedLayer` / `LayerRecipe` |
| **F3 — Bézier** | `BezierPatchInterpolator` + tesztek, subdivision dialógus osztott előnézettel, megjelenítési simítás mód | `RasterField.Core/Rasters`, `RasterView` |
| **F4 — Vektor levezetés** | Szintvonal dialógus (fő/alap, simítás, címkék, Bézier‑forrás), vízhálózat, stílus/osztályozás/címkézés | `ContourGenerator`, `HydrologyAnalysis`, `VectorLayer` render |
| **F5 — Elemzés** | Többpontos profil dokkban, többréteges pontazonosító, mérés, zonális statisztika | `RasterProfiler`, `ProfileWindow` → panel |
| **F6 — Projekt & formátumok** | `.rfproj`, Undo/Redo, GeoJSON/CSV import‑export | új `ProjectDocument` |

Minden fázis után: `dotnet build` figyelmeztetés nélkül (TreatWarningsAsErrors),
új Core funkciókhoz egységtesztek, README frissítés.

---

## 16. Elfogadási kritériumok (UX)

- [ ] Új felhasználó 1 percen belül megnyit egy `.ers`‑t, palettát vált és leolvas egy értéket — segítség nélkül.
- [ ] Szintvonal 3 kattintással: jobb‑klikk réteg → *Szintvonal…* → *Alkalmaz*.
- [ ] Bézier ×4 előnézet a látható kivágaton < 300 ms egy 2000×2000‑es rácson.
- [ ] Egy 200 MB‑os streaming rétegen minden eszköz (profil, azonosító, előnézet) működik, a UI nem fagy.
- [ ] Minden származtatott réteg megmondja, miből és milyen paraméterekkel készült.
- [ ] Mentetlen munka nem veszhet el figyelmeztetés nélkül.

---

## 17. Megvalósítás állapota

A terv minden pontja megvalósult; az alábbi táblázat azt is jelzi, ahol a megvalósítás eltér a tervtől.

| Terület | Állapot | Megjegyzés |
|---|---|---|
| **Háromoszlopos dokkolt elrendezés** | ✅ | Bal: rétegek + elemzés (Azonosítás / Profil / Mérés‑zóna fülek), közép: térkép, jobb: az aktív réteg tulajdonságai + jelmagyarázat. `F9` / `F10` / `F11` ki‑be kapcsol; a dokkszélességek mentődnek. |
| **Parancskereső** | ✅ | `Ctrl+K`; ékezet‑ és kisbetű‑független keresés a teljes menüben (pl. „szintv”, „bezier”). |
| **Menü, gyorsbillentyűk** | ✅ | Egy közös menümodell az ablakmenühöz és a macOS rendszermenühöz; a menüben látható gyorsbillentyűk automatikusan működnek. |
| **Rétegmodell** | ✅ | Raszter, RGB, vektor, származtatott (↳, ●), jobb‑klikk / ⋮ helyi menü a kártyákon. Csoportok helyett a sorrend + láthatóság + átlátszóság + keverés kezeli a rétegeket. |
| **Keverési módok, átlátszóság** | ✅ | Normál, Szorzás (árnyékoláshoz), Képernyő, Átfedés, Sötétítés, Világosítás, Lágy fény; átlátszóság a kártyán és a jobb panelen. |
| **Interaktív hisztogram** | ✅ | A jobb panelen, a paletta színeivel; a két fogantyú húzása állítja a széthúzást. |
| **Metaadat, ERS fejléc** | ✅ | Információ szekció + „Teljes ERS fejléc…” ablak. |
| **Levezetés receptből, újraszámolás** | ✅ | Bézier, szintvonal, vízhálózat, lejtő, kitettség, árnyékolás, görbület, lefolyás, sávszámítás: a „Paraméterek…” / ↻ újranyitja a párbeszédet kitöltve, és helyben újraszámol. Kivágás, mozaik, láthatóság, kitöltés recept nélküli. |
| **Bézier‑patch interpoláció** | ✅ | Core + párbeszéd előnézettel + megjelenítési simítás + szintvonal‑forrás + profil‑görbe + pontazonosító. Húzható elválasztós osztott előnézet helyett két kép egymás mellett. |
| **Szintvonal, vízhálózat** | ✅ | Fővonal + vonal menti felirat, Chaikin‑simítás, min. hossz, Bézier forrásfelszín; Strahler‑rend szerinti vonalvastagság. |
| **Pontazonosító** | ✅ | Az eredmény az Elemzés panel „Azonosítás” fülén: minden látható réteg (összes sáv, bilineáris, Bézier), vektoroknál a legközelebbi objektum. |
| **Többpontos profil** | ✅ | `P`; kattintás pontot ad, húzás mozgat, dupla kattintás / Enter lezár, Backspace visszavon. Élő diagram a dokkban, minden látható réteg + Bézier‑görbe, külön ablak és CSV‑export. Vektorvonal mentén is (⋮ ▸ Profil az első vonal mentén). |
| **Mérés** | ✅ | `M`; hossz, terepkövető (3D) felszíni hossz, lezárva kerület és terület. |
| **Zonális statisztika** | ✅ | `Z`: rajzolt sokszögben minden látható raszterre; illetve poligonrétegből soronként, CSV‑exporttal. |
| **Vektor első rétegként** | ✅ | Láthatatlan „keret” réteg adja a koordinátarendszert, amíg nincs raszter; az első raszter érkezésekor eltűnik. |
| **GeoJSON / CSV** | ✅ | Import (Réteg hozzáadása, fogd‑és‑vidd, parancssor) és export (⋮ menü). Átvetítés nincs: a koordináták a térkép rendszerében értendők. |
| **Projektfájl (`.rfproj`)** | ✅ | Rétegek, megjelenítés, keverés, receptek, nézet, könyvjelzők; relatív útvonalak. A mentetlen származtatott rétegek receptként tárolódnak, és megnyitáskor újraszámolódnak. |
| **Visszavonás / újra** | ✅ | `Ctrl+Z` / `Ctrl+Y`, 100 lépés; a csúszkák egy lépésbe vonódnak össze. Réteg hozzáadás/eltávolítás, sorrend, láthatóság, megjelenítés, stílus, újraszámolás. |
| **Könyvjelzők, ugrás koordinátára** | ✅ | `Ctrl+Shift+D`, `Ctrl+1…9`; Nézet ▸ Ugrás koordinátára. |
| **Mentetlen munka védelme** | ✅ | Bezáráskor, új projektnél és projekt megnyitásakor figyelmeztet a csak memóriában létező rétegekre. |
| **Magyar / angol felület** | ✅ | Nézet ▸ Nyelv (automatikus / English / Magyar). A menük azonnal váltanak, a panelek és ablakok újraindítás után. |
