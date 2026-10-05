# -*- coding: utf-8 -*-
"""Discord als Quelle des Spielbilds (2026-09-29), 25 Sprachen. Nicht von Muttersprachlern geprueft."""

K = (
    "Watch the Xbox's stream in Discord on this PC, popped out or full screen. Your own stream needs a second Discord account here: your call moves to the Xbox.",
)

_UEBERSETZT = {
 "de": ("Schau den Stream der Xbox in Discord auf diesem PC an, herausgelöst oder im Vollbild. Für den eigenen Stream braucht es hier ein zweites Discord-Konto: dein Anruf wandert auf die Xbox.",),
 "fr": ("Regarde le stream de la Xbox dans Discord sur ce PC, détaché ou en plein écran. Pour ton propre stream, il faut ici un second compte Discord : ton appel passe sur la Xbox.",),
 "es": ("Mira el stream de la Xbox en Discord en este PC, separado o a pantalla completa. Para tu propio stream hace falta aquí una segunda cuenta de Discord: tu llamada pasa a la Xbox.",),
 "it": ("Guarda lo stream della Xbox in Discord su questo PC, staccato o a schermo intero. Per il tuo stream serve qui un secondo account Discord: la tua chiamata passa alla Xbox.",),
 "pt": ("Vê o stream da Xbox no Discord neste PC, destacado ou em ecrã inteiro. Para o teu próprio stream precisas aqui de uma segunda conta Discord: a tua chamada passa para a Xbox.",),
 "nl": ("Bekijk de stream van de Xbox in Discord op deze pc, losgemaakt of op volledig scherm. Voor je eigen stream heb je hier een tweede Discord-account nodig: je gesprek gaat naar de Xbox.",),
 "pl": ("Oglądaj stream z Xboksa w Discordzie na tym PC, w osobnym oknie lub na pełnym ekranie. Do własnego streamu potrzebne jest tu drugie konto Discord: twoja rozmowa przechodzi na Xboksa.",),
 "cs": ("Sleduj stream z Xboxu v Discordu na tomto PC, v samostatném okně nebo na celou obrazovku. Pro vlastní stream tu potřebuješ druhý účet Discord: tvůj hovor přejde na Xbox.",),
 "da": ("Se Xboxens stream i Discord på denne pc, i eget vindue eller i fuld skærm. Til din egen stream skal der bruges en anden Discord-konto her: dit opkald flytter over på Xboxen.",),
 "sv": ("Titta på Xboxens stream i Discord på den här datorn, i eget fönster eller i helskärm. För din egen stream behövs ett andra Discord-konto här: ditt samtal flyttar till Xboxen.",),
 "fi": ("Katso Xboxin striimiä Discordissa tällä PC:llä, irrotettuna tai koko näytöllä. Omaa striimiäsi varten tarvitset tässä toisen Discord-tilin: puhelusi siirtyy Xboxille.",),
 "hu": ("Nézd az Xbox streamjét a Discordban ezen a PC-n, külön ablakban vagy teljes képernyőn. A saját streamedhez itt második Discord-fiók kell: a hívásod átkerül az Xboxra.",),
 "ro": ("Urmărește streamul Xbox-ului în Discord pe acest PC, în fereastră separată sau pe tot ecranul. Pentru propriul stream ai nevoie aici de un al doilea cont Discord: apelul tău trece pe Xbox.",),
 "el": ("Δες τη ροή του Xbox στο Discord σε αυτό το PC, σε ξεχωριστό παράθυρο ή σε πλήρη οθόνη. Για τη δική σου ροή χρειάζεται εδώ δεύτερος λογαριασμός Discord: η κλήση σου μεταφέρεται στο Xbox.",),
 "tr": ("Xbox'ın yayınını bu PC'de Discord'da ayrı pencerede veya tam ekranda izle. Kendi yayının için burada ikinci bir Discord hesabı gerekir: araman Xbox'a geçer.",),
 "ru": ("Смотри трансляцию с Xbox в Discord на этом ПК — в отдельном окне или на весь экран. Для своей трансляции здесь нужен второй аккаунт Discord: твой звонок переходит на Xbox.",),
 "ja": ("この PC の Discord で Xbox の配信を、ポップアウトか全画面で表示してください。自分の配信を見るにはここで2つ目の Discord アカウントが必要です：通話が Xbox に移るためです。",),
 "ko": ("이 PC의 Discord에서 Xbox 방송을 분리된 창이나 전체 화면으로 보세요. 내 방송을 보려면 여기서 두 번째 Discord 계정이 필요합니다: 통화가 Xbox로 옮겨가기 때문입니다.",),
 "zh-Hans": ("在这台电脑的 Discord 中观看 Xbox 的直播，弹出窗口或全屏均可。要看你自己的直播，这里需要第二个 Discord 账号：你的通话会转到 Xbox 上。",),
 "zh-Hant": ("在這台電腦的 Discord 中觀看 Xbox 的直播，彈出視窗或全螢幕皆可。要看你自己的直播，這裡需要第二個 Discord 帳號：你的通話會轉到 Xbox 上。",),
 "th": ("ดูสตรีมของ Xbox ใน Discord บนพีซีเครื่องนี้ แบบแยกหน้าต่างหรือเต็มจอ หากเป็นสตรีมของคุณเอง ต้องใช้บัญชี Discord บัญชีที่สองที่นี่ เพราะการโทรของคุณย้ายไปที่ Xbox",),
 "vi": ("Xem luồng của Xbox trong Discord trên PC này, ở cửa sổ riêng hoặc toàn màn hình. Với luồng của chính bạn, cần một tài khoản Discord thứ hai ở đây: cuộc gọi của bạn chuyển sang Xbox.",),
 "id": ("Tonton stream Xbox di Discord pada PC ini, di jendela terpisah atau layar penuh. Untuk stream milikmu sendiri perlu akun Discord kedua di sini: panggilanmu pindah ke Xbox.",),
 "ms": ("Tonton strim Xbox dalam Discord pada PC ini, dalam tetingkap berasingan atau skrin penuh. Untuk strim anda sendiri, akaun Discord kedua diperlukan di sini: panggilan anda berpindah ke Xbox.",),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
