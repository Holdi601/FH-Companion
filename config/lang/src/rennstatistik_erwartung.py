# -*- coding: utf-8 -*-
"""Rennstatistik: gegen die Erwartung -- Platz nach der Bestenliste gegen den tatsaechlichen (2026-10-01). 25 Sprachen, nicht von Muttersprachlern geprueft."""

K = {
    "titel": "Vs expectation",
    "unter": "{0} better, {1} as expected, {2} worse than the leaderboard says, in {3} race(s)",
}

_UEBERSETZT = {
 "de": {"titel": "Gegen die Erwartung", "unter": "{0} besser, {1} wie erwartet, {2} schlechter als die Bestenliste sagt, in {3} Rennen"},
 "fr": {"titel": "Face aux attentes", "unter": "{0} mieux, {1} comme prévu, {2} moins bien que ne l'indique le classement, sur {3} course(s)"},
 "es": {"titel": "Frente a lo esperado", "unter": "{0} mejor, {1} como se esperaba, {2} peor de lo que dice la clasificación, en {3} carrera(s)"},
 "it": {"titel": "Rispetto alle attese", "unter": "{0} meglio, {1} come previsto, {2} peggio di quanto dica la classifica, in {3} gare"},
 "pt": {"titel": "Contra o esperado", "unter": "{0} melhor, {1} como esperado, {2} pior do que a classificação indica, em {3} corrida(s)"},
 "nl": {"titel": "Tegen de verwachting", "unter": "{0} beter, {1} zoals verwacht, {2} slechter dan de ranglijst zegt, in {3} race(s)"},
 "pl": {"titel": "Wobec oczekiwań", "unter": "{0} lepiej, {1} zgodnie z oczekiwaniem, {2} gorzej niż wynika z tabeli, w {3} wyścigach"},
 "cs": {"titel": "Proti očekávání", "unter": "{0} lépe, {1} podle očekávání, {2} hůře, než říká žebříček, v {3} závodech"},
 "da": {"titel": "Mod forventningen", "unter": "{0} bedre, {1} som forventet, {2} dårligere end ranglisten siger, i {3} løb"},
 "sv": {"titel": "Mot förväntan", "unter": "{0} bättre, {1} som väntat, {2} sämre än topplistan säger, i {3} lopp"},
 "fi": {"titel": "Odotuksiin nähden", "unter": "{0} paremmin, {1} odotetusti, {2} huonommin kuin tulostaulu sanoo, {3} kilpailussa"},
 "hu": {"titel": "A várakozáshoz képest", "unter": "{0} jobb, {1} a várt, {2} rosszabb, mint amit a ranglista mutat, {3} versenyben"},
 "ro": {"titel": "Față de așteptări", "unter": "{0} mai bine, {1} cum era de așteptat, {2} mai slab decât spune clasamentul, în {3} curse"},
 "el": {"titel": "Σε σχέση με την πρόβλεψη", "unter": "{0} καλύτερα, {1} όπως αναμενόταν, {2} χειρότερα από ό,τι λέει η κατάταξη, σε {3} αγώνες"},
 "tr": {"titel": "Beklentiye göre", "unter": "{3} yarışta {0} daha iyi, {1} beklendiği gibi, {2} sıralamanın söylediğinden kötü"},
 "ru": {"titel": "Против ожиданий", "unter": "{0} лучше, {1} как ожидалось, {2} хуже, чем по таблице, в {3} гонках"},
 "ja": {"titel": "予想との比較", "unter": "{3} レース中：予想より上 {0}、予想どおり {1}、予想より下 {2}"},
 "ko": {"titel": "예상 대비", "unter": "레이스 {3}회 중 예상보다 좋음 {0}, 예상대로 {1}, 리더보드 예상보다 나쁨 {2}"},
 "zh-Hans": {"titel": "对比预期", "unter": "{3} 场比赛中：好于排行榜预期 {0}，符合预期 {1}，差于预期 {2}"},
 "zh-Hant": {"titel": "對比預期", "unter": "{3} 場比賽中：優於排行榜預期 {0}，符合預期 {1}，差於預期 {2}"},
 "th": {"titel": "เทียบกับที่คาด", "unter": "ดีกว่า {0}, ตามคาด {1}, แย่กว่าที่กระดานผู้นำบอก {2} จาก {3} การแข่ง"},
 "vi": {"titel": "So với kỳ vọng", "unter": "{0} tốt hơn, {1} đúng kỳ vọng, {2} kém hơn bảng xếp hạng dự đoán, trong {3} cuộc đua"},
 "id": {"titel": "Dibanding perkiraan", "unter": "{0} lebih baik, {1} sesuai perkiraan, {2} lebih buruk dari papan peringkat, dalam {3} balapan"},
 "ms": {"titel": "Berbanding jangkaan", "unter": "{0} lebih baik, {1} seperti dijangka, {2} lebih teruk daripada papan pendahulu, dalam {3} perlumbaan"},
 "nb": {"titel": "Mot forventningen", "unter": "{0} bedre, {1} som forventet, {2} dårligere enn resultatlisten tilsier, i {3} løp"},
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _id, _text in _saetze.items():
        for _i in range(6):
            assert ("{%d}" % _i in K[_id]) == ("{%d}" % _i in _text), (_code, _id, _i)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
