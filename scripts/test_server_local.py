"""Den echten Server lokal starten und von aussen pruefen -- HTTP UND HTTPS.

    python scripts/test_server_local.py

Startet serve_analytics.py auf einem freien Port mit einem Wegwerf-Zertifikat und
prueft, was nur am laufenden Server zu sehen ist: beide Protokolle auf einem Port,
ein haengender TLS-Handschlag, der niemand anderen aufhaelt, die Bremse, die
Sicherheitskoepfe und der gestreamte Datensatz.
"""
from __future__ import annotations

import datetime
import http.client
import socket
import ssl
import subprocess
import sys
import tempfile
import time
from pathlib import Path

WS = Path(__file__).resolve().parent.parent
fehler = 0


def pruefe(gut: bool, text: str) -> None:
    global fehler
    print(("  ok   " if gut else "  FEHL ") + text)
    if not gut:
        fehler += 1


def zertifikat(ordner: Path) -> tuple[Path, Path]:
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID
    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
    jetzt = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
            .public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(jetzt - datetime.timedelta(minutes=5))
            .not_valid_after(jetzt + datetime.timedelta(days=1))
            .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), False)
            .sign(key, hashes.SHA256()))
    c, k = ordner / "fullchain.pem", ordner / "privkey.pem"
    c.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    k.write_bytes(key.private_bytes(serialization.Encoding.PEM,
                                    serialization.PrivateFormat.PKCS8,
                                    serialization.NoEncryption()))
    return c, k


def freier_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


tmp = Path(tempfile.mkdtemp())
cert, key = zertifikat(tmp)
port = freier_port()
server = subprocess.Popen(
    [sys.executable, str(WS / "server" / "serve_analytics.py"), "--host", "127.0.0.1",
     "--port", str(port), "--nur-ausliefern", "--tls-cert", str(cert), "--tls-key", str(key)],
    cwd=str(WS), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
try:
    for _ in range(50):
        try:
            socket.create_connection(("127.0.0.1", port), timeout=0.2).close()
            break
        except OSError:
            time.sleep(0.2)

    def hole(pfad: str, tls: bool = False, methode: str = "GET"):
        if tls:
            ctx = ssl.create_default_context()
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
            c = http.client.HTTPSConnection("127.0.0.1", port, context=ctx, timeout=20)
        else:
            c = http.client.HTTPConnection("127.0.0.1", port, timeout=20)
        c.request(methode, pfad)
        r = c.getresponse()
        body = r.read()
        c.close()
        return r.status, dict(r.getheaders()), body

    st, kopf, _ = hole("/status")
    pruefe(st == 200, f"HTTP auf dem Port ({st})")
    st2, kopf2, _ = hole("/status", tls=True)
    pruefe(st2 == 200, f"HTTPS auf demselben Port ({st2})")
    pruefe("Strict-Transport-Security" in kopf2 and "Strict-Transport-Security" not in kopf,
           "HSTS nur ueber HTTPS")
    pruefe(kopf.get("X-Content-Type-Options") == "nosniff" and kopf.get("X-Frame-Options") == "DENY",
           "Sicherheitskoepfe gesetzt")
    pruefe("Python" not in kopf.get("Server", ""), f"Server verraet keine Fassung ({kopf.get('Server')})")

    # TLS SAUBER BEENDET: mit suppress_ragged_eofs=False wirft ein Ende ohne
    # close_notify SSLEOFError -- genau das, was curl/Schannel als "server closed
    # abruptly" meldete. Ein sauberes Ende liefert b"".
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    roh = ctx.wrap_socket(socket.create_connection(("127.0.0.1", port), timeout=10),
                          suppress_ragged_eofs=False)
    roh.sendall(b"GET /status HTTP/1.0\r\nHost: localhost\r\n\r\n")
    sauber = True
    try:
        while roh.recv(65536):
            pass
    except ssl.SSLEOFError:
        sauber = False
    roh.close()
    pruefe(sauber, "HTTPS endet mit close_notify, nicht abrupt")

    st, kopf, body = hole("/admin")
    csp = kopf.get("Content-Security-Policy", "")
    pruefe(st == 200 and "frame-ancestors 'none'" in csp and "connect-src 'self'" in csp,
           "Verwaltungsseite mit strenger CSP")

    # EIN HAENGENDER HANDSCHLAG DARF NIEMANDEN AUFHALTEN.
    haenger = socket.create_connection(("127.0.0.1", port))
    haenger.sendall(b"\x16")                       # TLS begonnen, dann Stille
    t0 = time.monotonic()
    st, _, _ = hole("/status")
    dauer = time.monotonic() - t0
    pruefe(st == 200 and dauer < 2, f"haengender Handschlag blockiert nicht ({dauer:.2f} s)")
    haenger.close()

    st, kopf, body = hole("/api/dataset")
    pruefe(st in (200, 503), f"/api/dataset antwortet ({st}, {len(body)} Bytes)")
    if st == 200:
        pruefe(int(kopf.get("Content-Length", -1)) == len(body), "Datensatz mit Laengenangabe gestreamt")

    # DIE BREMSE: viele schnelle Anfragen derselben Adresse.
    stati = [hole("/api/summary")[0] for _ in range(135)]
    pruefe(429 in stati and stati.count(429) >= 10, f"Bremse greift ({stati.count(429)} x 429 von 135)")

    st, kopf, _ = hole("/rivals_auto_wertung.html/../../config/contrib_keys.json")
    pruefe(st in (404, 429), f"Pfad aus dem Seitenordner heraus abgewiesen ({st})")
finally:
    server.terminate()
    try:
        out = server.communicate(timeout=10)[0]
    except Exception:
        out = ""
    if "Traceback" in (out or ""):
        print("  Serverprotokoll:\n" + out[-1500:])
        fehler += 1

print("Alle Pruefungen bestanden." if fehler == 0 else f"{fehler} Pruefung(en) FEHLGESCHLAGEN.")
sys.exit(1 if fehler else 0)
