namespace NowBar.App.Helpers;

/// <summary>Open-Meteo가 사용하는 WMO 날씨 코드 → 한국어 설명/아이콘.</summary>
public static class WeatherCodes
{
    public static string Describe(int code) => code switch
    {
        0 => "맑음",
        1 => "대체로 맑음",
        2 => "구름 조금",
        3 => "흐림",
        45 or 48 => "안개",
        51 or 53 or 55 => "이슬비",
        56 or 57 => "어는 이슬비",
        61 => "약한 비",
        63 => "비",
        65 => "강한 비",
        66 or 67 => "어는 비",
        71 => "약한 눈",
        73 => "눈",
        75 => "강한 눈",
        77 => "싸락눈",
        80 or 81 => "소나기",
        82 => "강한 소나기",
        85 or 86 => "눈 소나기",
        95 => "뇌우",
        96 or 99 => "우박을 동반한 뇌우",
        _ => "알 수 없음",
    };

    /// <summary>Resources/Icons/Icons.xaml 의 "Icon.{이름}" 키를 반환한다.</summary>
    public static string IconKind(int code, bool isDay) => code switch
    {
        0 or 1 => isDay ? "Sun" : "Moon",
        2 => isDay ? "CloudSun" : "Cloud",
        3 => "Cloud",
        45 or 48 => "CloudFog",
        51 or 53 or 55 or 56 or 57 => "CloudDrizzle",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "CloudRain",
        71 or 73 or 75 or 77 or 85 or 86 => "CloudSnow",
        95 or 96 or 99 => "CloudLightning",
        _ => "Cloud",
    };
}
