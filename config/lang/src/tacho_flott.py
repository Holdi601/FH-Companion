# -*- coding: utf-8 -*-
"""Tacho: Bildrate wie der Bildschirm, Schaltpfeil, bester Gang (2026-10-18), 25 Sprachen, nicht von
Muttersprachlern geprueft. Schluessel aus der Liste der fehlenden Saetze uebernommen, nicht abgetippt.
"Lap delta HUD" heisst wie der Reiter in der jeweiligen Sprachdatei."""

K = {
    "like": "like the screen",
    "pos": "Position and size: drag it in the tab Lap delta HUD. It draws on its own thread, only when something changes, at most as often as the screen refreshes.",
    "shift": "Shift advice: arrow up or down at the gear",
    "best": "Best gear for the speed next to the gear",
}

_UEBERSETZT = {
 "de": {
  "like": "wie der Bildschirm",
  "pos": "Position und Größe: im Reiter Rundendelta-HUD ziehen. Er zeichnet in einem eigenen Thread, nur wenn sich etwas ändert, höchstens so oft, wie der Bildschirm aktualisiert.",
  "shift": "Schaltempfehlung: Pfeil hoch oder runter am Gang",
  "best": "Bester Gang für das Tempo neben dem Gang",
 },
 "fr": {
  "like": "comme l'écran",
  "pos": "Position et taille : fais-le glisser dans l'onglet HUD delta au tour. Il se dessine dans son propre thread, seulement quand quelque chose change, au plus aussi souvent que l'écran se rafraîchit.",
  "shift": "Conseil de passage : flèche vers le haut ou le bas au rapport",
  "best": "Meilleur rapport pour la vitesse à côté du rapport",
 },
 "es": {
  "like": "como la pantalla",
  "pos": "Posición y tamaño: arrástralo en la pestaña HUD de delta por vuelta. Se dibuja en su propio hilo, solo cuando algo cambia, como mucho tan a menudo como se refresca la pantalla.",
  "shift": "Consejo de cambio: flecha arriba o abajo junto a la marcha",
  "best": "Mejor marcha para la velocidad junto a la marcha",
 },
 "it": {
  "like": "come lo schermo",
  "pos": "Posizione e dimensione: trascinalo nella scheda HUD delta sul giro. Si disegna in un proprio thread, solo quando cambia qualcosa, al massimo con la frequenza di aggiornamento dello schermo.",
  "shift": "Consiglio di cambiata: freccia su o giù accanto alla marcia",
  "best": "Marcia migliore per la velocità accanto alla marcia",
 },
 "pt": {
  "like": "como o ecrã",
  "pos": "Posição e tamanho: arrasta-o no separador HUD de delta da volta. Desenha-se no seu próprio thread, só quando algo muda, no máximo tantas vezes quanto o ecrã atualiza.",
  "shift": "Conselho de mudança: seta para cima ou para baixo junto à mudança",
  "best": "Melhor mudança para a velocidade ao lado da mudança",
 },
 "nl": {
  "like": "zoals het scherm",
  "pos": "Positie en grootte: sleep hem in het tabblad Rondedelta-HUD. Hij tekent in een eigen thread, alleen als er iets verandert, hooguit zo vaak als het scherm ververst.",
  "shift": "Schakeladvies: pijl omhoog of omlaag bij de versnelling",
  "best": "Beste versnelling voor de snelheid naast de versnelling",
 },
 "pl": {
  "like": "jak ekran",
  "pos": "Pozycja i rozmiar: przeciągnij go w karcie HUD delty okrążenia. Rysuje się we własnym wątku, tylko gdy coś się zmienia, najwyżej tak często, jak odświeża się ekran.",
  "shift": "Podpowiedź zmiany biegu: strzałka w górę lub w dół przy biegu",
  "best": "Najlepszy bieg dla prędkości obok biegu",
 },
 "cs": {
  "like": "jako obrazovka",
  "pos": "Poloha a velikost: přetáhni ho na kartě HUD delta kola. Kreslí se ve vlastním vlákně, jen když se něco změní, nejvýše tak často, jak se obnovuje obrazovka.",
  "shift": "Rada k řazení: šipka nahoru nebo dolů u stupně",
  "best": "Nejlepší stupeň pro rychlost vedle stupně",
 },
 "da": {
  "like": "som skærmen",
  "pos": "Position og størrelse: træk den under fanen Omgangsdelta-HUD. Den tegner i sin egen tråd, kun når noget ændrer sig, højst så ofte som skærmen opdateres.",
  "shift": "Gearskifteråd: pil op eller ned ved gearet",
  "best": "Bedste gear til hastigheden ved siden af gearet",
 },
 "sv": {
  "like": "som skärmen",
  "pos": "Position och storlek: dra den under fliken Varvdelta-HUD. Den ritar i en egen tråd, bara när något ändras, högst så ofta som skärmen uppdateras.",
  "shift": "Växlingsråd: pil upp eller ner vid växeln",
  "best": "Bästa växel för hastigheten bredvid växeln",
 },
 "nb": {
  "like": "som skjermen",
  "pos": "Posisjon og størrelse: dra den i fanen Rundedelta-HUD. Den tegner i sin egen tråd, bare når noe endrer seg, høyst så ofte som skjermen oppdateres.",
  "shift": "Girråd: pil opp eller ned ved giret",
  "best": "Beste gir for farten ved siden av giret",
 },
 "fi": {
  "like": "kuten näyttö",
  "pos": "Sijainti ja koko: vedä sitä välilehdellä Kierrosdelta-HUD. Se piirtyy omassa säikeessään, vain kun jokin muuttuu, enintään niin usein kuin näyttö päivittyy.",
  "shift": "Vaihtosuositus: nuoli ylös tai alas vaihteen kohdalla",
  "best": "Paras vaihde nopeudelle vaihteen vieressä",
 },
 "hu": {
  "like": "mint a képernyő",
  "pos": "Helyzet és méret: húzd a Kördelta HUD lapon. Saját szálon rajzol, csak ha valami változik, legfeljebb olyan gyakran, ahogy a képernyő frissül.",
  "shift": "Váltási javaslat: fel vagy le nyíl a fokozatnál",
  "best": "A sebességhez legjobb fokozat a fokozat mellett",
 },
 "ro": {
  "like": "ca ecranul",
  "pos": "Poziție și dimensiune: trage-l în fila HUD delta pe tur. Se desenează pe propriul fir de execuție, doar când se schimbă ceva, cel mult cât de des se reîmprospătează ecranul.",
  "shift": "Sfat de schimbare: săgeată în sus sau în jos la treaptă",
  "best": "Cea mai bună treaptă pentru viteză lângă treaptă",
 },
 "el": {
  "like": "όπως η οθόνη",
  "pos": "Θέση και μέγεθος: σύρε το στην καρτέλα HUD delta γύρου. Σχεδιάζεται στο δικό του νήμα, μόνο όταν κάτι αλλάζει, το πολύ τόσο συχνά όσο ανανεώνεται η οθόνη.",
  "shift": "Συμβουλή αλλαγής: βέλος πάνω ή κάτω στη σχέση",
  "best": "Καλύτερη σχέση για την ταχύτητα δίπλα στη σχέση",
 },
 "ru": {
  "like": "как у экрана",
  "pos": "Положение и размер: перетащите его на вкладке «HUD дельты круга». Он рисуется в собственном потоке, только когда что-то меняется, и не чаще, чем обновляется экран.",
  "shift": "Подсказка переключения: стрелка вверх или вниз у передачи",
  "best": "Лучшая передача для скорости рядом с передачей",
 },
 "tr": {
  "like": "ekran gibi",
  "pos": "Konum ve boyut: Tur delta HUD'u sekmesinde sürükle. Kendi iş parçacığında, yalnızca bir şey değiştiğinde ve en fazla ekranın yenilenme sıklığında çizilir.",
  "shift": "Vites önerisi: viteste yukarı veya aşağı ok",
  "best": "Vitesin yanında hıza en uygun vites",
 },
 "id": {
  "like": "seperti layar",
  "pos": "Posisi dan ukuran: seret di tab HUD delta lap. Ia digambar di thread-nya sendiri, hanya saat ada yang berubah, paling sering sesuai laju penyegaran layar.",
  "shift": "Saran pindah gigi: panah naik atau turun di gigi",
  "best": "Gigi terbaik untuk kecepatan di samping gigi",
 },
 "ms": {
  "like": "seperti skrin",
  "pos": "Kedudukan dan saiz: seretnya dalam tab HUD delta pusingan. Ia dilukis pada benangnya sendiri, hanya apabila sesuatu berubah, paling kerap sekerap skrin disegarkan.",
  "shift": "Nasihat tukar gear: anak panah naik atau turun pada gear",
  "best": "Gear terbaik untuk kelajuan di sebelah gear",
 },
 "vi": {
  "like": "theo màn hình",
  "pos": "Vị trí và kích thước: kéo nó trong thẻ HUD delta vòng. Nó được vẽ trên luồng riêng, chỉ khi có gì thay đổi, nhiều nhất bằng tần số làm mới của màn hình.",
  "shift": "Gợi ý sang số: mũi tên lên hoặc xuống ở số",
  "best": "Số tốt nhất cho tốc độ bên cạnh số",
 },
 "th": {
  "like": "ตามหน้าจอ",
  "pos": "ตำแหน่งและขนาด: ลากในแท็บ HUD เดลต้ารอบ มันวาดในเธรดของตัวเอง เฉพาะเมื่อมีบางอย่างเปลี่ยน และบ่อยที่สุดเท่ากับอัตรารีเฟรชของหน้าจอ",
  "shift": "คำแนะนำเปลี่ยนเกียร์: ลูกศรขึ้นหรือลงที่เกียร์",
  "best": "เกียร์ที่ดีที่สุดสำหรับความเร็วข้างเกียร์",
 },
 "ja": {
  "like": "画面に合わせる",
  "pos": "位置とサイズ：「ラップデルタ HUD」タブでドラッグします。専用スレッドで、何かが変わったときだけ、最大で画面のリフレッシュレートまで描画します。",
  "shift": "シフト案内：ギアの横に上下の矢印",
  "best": "速度に最適なギアをギアの横に",
 },
 "ko": {
  "like": "화면과 같게",
  "pos": "위치와 크기: 랩 델타 HUD 탭에서 끌어서 조정하세요. 자체 스레드에서, 무언가 바뀔 때만, 최대 화면 주사율만큼 그립니다.",
  "shift": "변속 안내: 기어 옆 위/아래 화살표",
  "best": "기어 옆에 속도에 맞는 최적 기어",
 },
 "zh-Hans": {
  "like": "跟随屏幕",
  "pos": "位置和大小：在“圈速差值 HUD”选项卡中拖动。它在自己的线程中绘制，只在有变化时绘制，频率最高等于屏幕刷新率。",
  "shift": "换挡提示：挡位旁的上下箭头",
  "best": "在挡位旁显示适合当前速度的最佳挡位",
 },
 "zh-Hant": {
  "like": "跟隨螢幕",
  "pos": "位置與大小：在「圈速差值 HUD」分頁中拖曳。它在自己的執行緒中繪製，只在有變化時繪製，頻率最高等於螢幕更新率。",
  "shift": "換檔提示：檔位旁的上下箭頭",
  "best": "在檔位旁顯示適合目前速度的最佳檔位",
 },
}

import re as _re
_FELD = _re.compile(r"\{\d+(?::[^}]*)?\}")
for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _id, _en in K.items():
        assert sorted(_FELD.findall(_en)) == sorted(_FELD.findall(_saetze[_id])), (_code, _id)
        assert _saetze[_id].strip(), (_code, _id)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
