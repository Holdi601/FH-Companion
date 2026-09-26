# Die Laufzeit fuer den Spiegel auf dem GNAS.
#
# WARUM UEBERHAUPT EIN CONTAINER, wo Python auf dem GNAS liegt: dort laeuft
# Debian 11 mit Python 3.9. Unser Code setzt an mehreren Stellen 3.10er Schreibweisen
# ein (`dict | None` ausserhalb von Annotationen, match-Ausdruecke in den Bauern).
# Auf 3.9 faellt das nicht beim Start auf, sondern beim ersten Aufruf, der die Zeile
# trifft -- also genau dann, wenn jemand die Seite benutzt. Der Container legt die
# Laufzeit fest und macht das NAS austauschbar.
#
# WARUM KEIN COPY DES CODES: das Verzeichnis wird hineingereicht. Ein Update ist
# damit ein Dateikopieren und ein Neustart des Containers -- kein Image-Bau ueber
# eine Leitung, auf der 66 MB Paket ohnehin schon liegen.
FROM python:3.12-slim

# pandas/pyarrow braucht nur import_contrib, und nur beim Einlesen einer Abgabe --
# aber genau das ist der eine Weg, auf dem hier ueberhaupt etwas geschrieben wird.
# Fehlt es, scheitert die Abgabe erst NACH dem Hochladen der 200 MB.
RUN pip install --no-cache-dir pandas==2.2.3 pyarrow==17.0.0

# Land und Region der Besucher aus einer LOKALEN Datenbank (server/geoip.py) -- die
# Adresse geht an keinen fremden Dienst. Ohne das Paket zaehlt visits.py weiter,
# nur ohne Land.
RUN pip install --no-cache-dir maxminddb==3.2.0

WORKDIR /app
EXPOSE 8787

# Kein Root: der Container schreibt Fremdbeitraege in ein Verzeichnis, das die
# Pipeline danach wieder abholt. Als Root geschriebene Dateien koennte das Konto
# auf dem NAS nicht lesen -- die Abgabe waere angekommen und trotzdem nicht zu gebrauchen.
USER 1000:1000

CMD ["python", "server/serve_analytics.py", \
     "--host", "0.0.0.0", "--port", "8787", "--nur-ausliefern"]
