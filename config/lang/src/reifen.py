# -*- coding: utf-8 -*-
"""Die Reifenuebersicht -- Schalter im Reiter "Lap delta HUD" und Titel des Overlays (2026-09-28), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = (
    "Tyre overview while driving",
    "Tyre temperature in Fahrenheit",
    "Tyres",
)

_UEBERSETZT = {
 "de": ("Reifenübersicht beim Fahren", "Reifentemperatur in Fahrenheit", "Reifen"),
 "fr": ("Vue des pneus pendant la conduite", "Température des pneus en Fahrenheit", "Pneus"),
 "es": ("Vista de neumáticos al conducir", "Temperatura de neumáticos en Fahrenheit", "Neumáticos"),
 "it": ("Panoramica gomme durante la guida", "Temperatura gomme in Fahrenheit", "Gomme"),
 "pt": ("Visão dos pneus ao conduzir", "Temperatura dos pneus em Fahrenheit", "Pneus"),
 "nl": ("Bandenoverzicht tijdens het rijden", "Bandentemperatuur in Fahrenheit", "Banden"),
 "pl": ("Podgląd opon podczas jazdy", "Temperatura opon w stopniach Fahrenheita", "Opony"),
 "sv": ("Däcköversikt under körning", "Däcktemperatur i Fahrenheit", "Däck"),
 "da": ("Dækoversigt under kørsel", "Dæktemperatur i Fahrenheit", "Dæk"),
 "fi": ("Rengasnäkymä ajon aikana", "Renkaiden lämpötila Fahrenheit-asteina", "Renkaat"),
 "cs": ("Přehled pneumatik při jízdě", "Teplota pneumatik ve stupních Fahrenheita", "Pneumatiky"),
 "hu": ("Gumiáttekintés vezetés közben", "Gumihőmérséklet Fahrenheitben", "Gumik"),
 "ro": ("Prezentare anvelope în timpul condusului", "Temperatura anvelopelor în Fahrenheit", "Anvelope"),
 "el": ("Επισκόπηση ελαστικών κατά την οδήγηση", "Θερμοκρασία ελαστικών σε Φαρενάιτ", "Ελαστικά"),
 "ru": ("Обзор шин во время езды", "Температура шин в градусах Фаренгейта", "Шины"),
 "tr": ("Sürüş sırasında lastik görünümü", "Lastik sıcaklığı Fahrenheit olarak", "Lastikler"),
 "id": ("Ikhtisar ban saat berkendara", "Suhu ban dalam Fahrenheit", "Ban"),
 "ms": ("Gambaran tayar semasa memandu", "Suhu tayar dalam Fahrenheit", "Tayar"),
 "vi": ("Tổng quan lốp khi lái", "Nhiệt độ lốp theo độ F", "Lốp"),
 "th": ("ภาพรวมยางขณะขับ", "อุณหภูมิยางเป็นฟาเรนไฮต์", "ยาง"),
 "ja": ("走行中のタイヤ概要", "タイヤ温度を華氏で表示", "タイヤ"),
 "ko": ("주행 중 타이어 개요", "타이어 온도를 화씨로 표시", "타이어"),
 "zh-Hans": ("驾驶时显示轮胎概览", "轮胎温度使用华氏度", "轮胎"),
 "zh-Hant": ("駕駛時顯示輪胎概覽", "輪胎溫度使用華氏度", "輪胎"),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {
    code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()
}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
