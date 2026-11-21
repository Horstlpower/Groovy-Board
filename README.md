# Groovy Board & Time Circuits

Zwei kleine Retro-Webseiten – ohne Server, einfach die HTML-Datei im Browser öffnen – plus ein Windows-Bildschirmschoner, der beide Seiten zeigt.

**▶ Live ausprobieren:** [Zeitmaschine](https://horstlpower.github.io/Groovy-Board/) · [Groovy Board](https://horstlpower.github.io/Groovy-Board/flipboard.html)

## 🕰️ Zeitmaschine – `index.html`
Zeitanzeige im Stil der berühmten Film-Zeitmaschine mit Siebensegment-Ziffern.

- **Zielzeit** (rot), **Gegenwart** (grün, läuft live) und **Zuletzt abgereist** (gelb)
- Zeiten einstellen per Tastenfeld (`TT MM JJJJ HH MM`, auch über die Tastatur), Datumsauswahl oder berühmte Ziele
- **Zeitreise:** Tacho beschleunigt auf 88 mph, der Fluxkompensator pulsiert, dann Blitz, Feuerspuren und Knall – die Anzeigen springen um

## ✈️ Groovy Board – `flipboard.html`
Split-Flap-Anzeige (22 × 6) wie früher am Flughafen, im Look der 70er-Jahre.

- Jede Kachel blättert Zeichen für Zeichen zum Ziel, dazu ein echtes Klack-Geräusch (abschaltbar)
- Eigene Texte mit automatischem Umbruch, Farbkacheln, Umlaute
- Playlist mit automatischem Wechsel, Vorlagen (Abflugtafel, Uhrzeit, Geburtstag …)
- Zweizeilige Überschrift, direkt anklickbar oder über die Felder änderbar

Alle Einstellungen werden nur im eigenen Browser gespeichert (localStorage).

## 🖥️ Bildschirmschoner (Windows 10/11)
Im Ordner [`Bildschirmschoner`](Bildschirmschoner) liegt **RetroZeit.scr**.

1. Das ganze Repository herunterladen. Der Ordner `Bildschirmschoner` muss neben `index.html` und `flipboard.html` liegen.
2. Rechtsklick auf `RetroZeit.scr` → **Installieren** (unter Windows 11 eventuell bei „Weitere Optionen anzeigen“).
3. Unter **Einstellungen** wählst du: Flipboard, Zeitmaschine oder abwechselnd, die Zeitabstände und den Ton.

Der Bildschirmschoner liest seine Texte aus [`flipboard-texte.txt`](flipboard-texte.txt):

```
TITEL: Groovy | Board        ← | trennt die zwei Zeilen der Überschrift
HALLO WELT
---                          ← trennt die Nachrichten
@CLOCK                       ← zeigt die aktuelle Uhrzeit
```

Farbkacheln: `{r}` rot, `{o}` orange, `{y}` gelb, `{g}` grün, `{b}` blau, `{v}` violett, `{n}` braun, `{w}` creme.

Benötigt die Microsoft-Edge-WebView2-Laufzeit, die in Windows 11 bereits enthalten ist. Beenden mit Mausbewegung oder Tastendruck.

### Selbst bauen
Der Quellcode liegt in [`screensaver-src`](screensaver-src) (C#, .NET Framework 4.8, WinForms + WebView2).

```powershell
dotnet build screensaver-src\RetroZeit.csproj -c Release -o Bildschirmschoner
Rename-Item Bildschirmschoner\RetroZeit.exe RetroZeit.scr
Rename-Item Bildschirmschoner\RetroZeit.exe.config RetroZeit.scr.config
```

## Hinweise
- Die Schriften (Google Fonts, DSEG) werden aus dem Internet geladen.
- Fan-Projekt ohne Verbindung zu Filmstudios oder Herstellern von Anzeigetafeln.
