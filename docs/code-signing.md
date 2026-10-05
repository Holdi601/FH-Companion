# „Unbekannter Herausgeber" loswerden

## Warum die Versionsangaben dafür nicht reichen

Die EXE trägt seit dem 2026-09-15 vollständige Versionsangaben:

```
CompanyName     : FH Companion
ProductName     : FH Companion
FileDescription : FH Companion
FileVersion     : 2026.9.15.0
LegalCopyright  : Free to use. No warranty.
```

Und trotzdem sagt Windows „Unbekannter Herausgeber". Nachgemessen:

```
Get-AuthenticodeSignature "...\FH Companion.exe"
Status : NotSigned
```

Das Herausgeberfeld kommt **ausschließlich aus der digitalen Signatur**, nie aus der
Versionsressource — und das ist richtig so: in die Versionsressource kann jeder
schreiben, was er will. Ein Schädling würde sich dort „Microsoft Corporation"
nennen. Nur eine Signatur, deren Kette zu einer vertrauenswürdigen Stelle führt,
ist eine Aussage, für die jemand geradesteht.

Es gibt also keinen Code-Trick. Es braucht ein Zertifikat.

## Die Wege, vom günstigsten zum teuersten

### 1. Azure Trusted Signing — rund 10 $ im Monat

Microsofts eigener Signaturdienst. Kein Hardware-Token, kein Zertifikat, das man
verwahren muss: signiert wird gegen einen Dienst, der den Schlüssel hält.

- **Kosten:** ein Basis-Tarif um die 10 $/Monat (Stand der Recherche; bitte
  aktuell nachsehen, Microsoft ändert die Tarife).
- **Voraussetzung:** Identitätsprüfung. Für Organisationen wird eine nachweisbare
  Historie von drei Jahren verlangt; für Einzelpersonen gibt es einen eigenen Weg.
- **Vorteil:** mit Abstand der billigste legitime Weg, und die Signatur zählt für
  SmartScreen-Ruf wie jede andere.
- **Nachteil:** die Einrichtung läuft über ein Azure-Konto und ist umständlicher
  als „Zertifikat kaufen, fertig".

Eingerichtet wird das hier so:

```json
{"modus": "trusted-signing",
 "endpoint": "https://eus.codesigning.azure.net",
 "dlib": "C:/pfad/Azure.CodeSigning.Dlib.dll",
 "metadata": "C:/pfad/metadata.json"}
```

### 2. Ein OV-Zertifikat für quelloffene Projekte — rund 100 € für ein bis drei Jahre

Einige Zertifizierungsstellen (Certum ist die bekannteste) geben verbilligte
Code-Signing-Zertifikate für quelloffene Projekte aus. Seit Juni 2023 muss der
private Schlüssel auf einer Hardware liegen, also kommt ein USB-Token mit.

- **Kosten:** deutlich unter den kommerziellen Preisen, dafür mit Nachweis, dass
  das Projekt offen ist.
- **Nachteil:** SmartScreen baut den Ruf erst über Downloads auf. Der
  Herausgebername steht aber sofort da, und das ist der Punkt hier.

### 3. Ein gewöhnliches OV-Zertifikat — 200 bis 400 € im Jahr

Sectigo, DigiCert, GlobalSign, SSL.com. Identitätsprüfung, Hardware-Token oder
Cloud-HSM. Funktioniert, kostet aber jedes Jahr wieder.

### 4. Ein EV-Zertifikat — 400 € im Jahr aufwärts

Der einzige Weg, bei dem **SmartScreen sofort** stillhält, ohne erst Ruf aufzubauen.
Für ein Hobbyprojekt schwer zu rechtfertigen.

### 5. Selbstsigniert — nur für einen bekannten Kreis

Für **eine Handvoll Freunde** ist das ein ehrlicher Weg, und er kostet nichts:

```powershell
# Einmalig, auf dem Baurechner:
$c = New-SelfSignedCertificate -Type CodeSigningCert `
     -Subject "CN=FH Companion" -CertStoreLocation Cert:\CurrentUser\My `
     -NotAfter (Get-Date).AddYears(5)
Export-Certificate -Cert $c -FilePath fh-companion.cer
```

Dann `config/signing.json` mit `{"modus":"thumbprint","thumbprint":"<Fingerabdruck>"}`
und `python scripts/sign_app.py`.

Der Freund muss die `.cer` **einmal** als vertrauenswürdig eintragen:

```powershell
# Braucht Administratorrechte. Nur tun, wenn man weiss, woher die Datei kommt.
Import-Certificate -FilePath fh-companion.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

**Das ist kein allgemeiner Rat.** Wer fremden Zertifikaten Vertrauen ausspricht,
vertraut allem, was damit signiert wird. Für einen Kreis, der ohnehin weiß, wer das
Programm gebaut hat, ist es vertretbar; für eine öffentliche Downloadseite nicht.

## Was Signieren NICHT löst

Der `Bearfoos.A!ml`-Fehlalarm ist eine Verhaltensbewertung. Eine Signatur macht ihn
unwahrscheinlicher — signierte Dateien werden milder beurteilt und bauen schneller
Ruf auf —, aber sie ist keine Garantie. Der direkte Weg dagegen bleibt die
Fehlalarm-Meldung, siehe `defender-false-positive.md`.

## Wenn ein Zertifikat da ist

```powershell
python scripts\sign_app.py --pruefen   # sieht nach, ob alles steht
python scripts\sign_app.py             # signiert EXE und DLL
```

Signiert werden nur die beiden Dateien, die wir selbst bauen. Die Laufzeitdateien
von .NET sind bereits von Microsoft signiert; sie noch einmal zu signieren wäre
falsch.

`config/signing.json`, `*.pfx`, `*.p12` und `*.snk` stehen in `.gitignore` und sind
über den Commit-Haken abgesichert. Ein Passwort steht dort nur als **Name einer
Umgebungsvariablen**, nie als Wert.
