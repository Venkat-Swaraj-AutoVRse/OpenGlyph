// SPDX-License-Identifier: MIT
// OpenGlyph benchmark text sets. These strings are ORIGINAL sample text written
// for this benchmark suite (not copied from any third-party test project). Each
// set targets ~2300 characters, matching the published UniText benchmark
// methodology (100 objects, ~2300 chars/object, Latin + Arabic + Hebrew + Mixed).
//
// Rationale for content: the Latin set is lorem-style original prose about text
// rendering; the Arabic and Hebrew sets are original neutral prose composed for
// shaping/BiDi exercise; the Mixed set interleaves all three scripts plus digits
// so BiDi reordering and script-run segmentation are exercised. TMP cannot shape
// Arabic/Hebrew without a plugin, so for TMP the Arabic/Hebrew/Mixed numbers are
// NOT apples-to-apples and the Latin-only set is the fair comparison (see RESULTS.md).

using System.Collections.Generic;

namespace OpenGlyph.Benchmarks
{
    public enum TextSetKind { Latin, Arabic, Hebrew, Mixed }

    public static class BenchmarkTexts
    {
        // ~2300 chars of original Latin prose (repeated/varied to reach length).
        public const string Latin =
            "Typography on a GPU is a study in trade-offs. A glyph is first an outline, a set of quadratic and cubic curves wound so that an even-odd or non-zero winding rule can decide what is inside the letter and what is not. To draw that outline at an arbitrary size without re-rasterizing it every frame, modern engines bake a signed distance field: each texel stores the distance to the nearest edge, and a cheap threshold in the fragment shader reconstructs a crisp contour at any scale. The cost moves from runtime to bake time, and the memory moves from many bitmaps to one atlas. Shaping is the other half of the problem. A run of code points is not a run of glyphs; ligatures fuse, marks stack, and the cursive scripts demand contextual forms that depend on neighbours. A text engine therefore separates concerns: segmentation splits the paragraph into runs of a single script and direction, the bidirectional algorithm orders those runs for display, the shaper maps clusters of code points to positioned glyphs, and the layout engine breaks lines, distributes leading, and aligns the result inside its box. Each of these stages wants to allocate, and allocation is the enemy of a smooth frame. The disciplined path pools its buffers, reuses its arrays, and measures the bytes it leaks per operation, because a garbage collection in the middle of a scroll is a visible stutter. Benchmarks exist to make these invisible costs legible. We instantiate a hundred objects, we warm the caches, we force a synchronous rebuild, and we time it with a stopwatch, reporting a median and a tail percentile so that one unlucky frame does not masquerade as the typical case. We change only the text and measure a full rebuild; we change only the width and measure a pure re-layout; we change only the colour and measure a mesh regeneration that touches no glyph positions. We count collections, we weigh allocations, and we compare the honest numbers side by side, refusing to copy a competitor's marketing claim as if it were a measurement. The point is not to win a chart but to understand where the time goes, so that the next optimization is aimed at the stage that actually dominates rather than the one that is merely easy to see. Good text is quiet work.";

        // ~2300 chars of original Arabic prose for shaping/BiDi exercise.
        public const string Arabic =
            "تُعَدُّ الطِّبَاعَةُ عَلَى مُعَالِجِ الرُّسُومِ دِرَاسَةً فِي المُقَايَضَاتِ. فَالحَرْفُ فِي أَصْلِهِ خَطٌّ خَارِجِيٌّ، مَجْمُوعَةٌ مِنَ المُنْحَنَيَاتِ تُلَفُّ حَتَّى تُقَرِّرَ قَاعِدَةُ المِلْءِ مَا هُوَ دَاخِلَ الحَرْفِ وَمَا هُوَ خَارِجَهُ. وَلِكَيْ نَرْسُمَ ذَلِكَ الخَطَّ بِأَيِّ حَجْمٍ دُونَ إِعَادَةِ تَوْلِيدِهِ فِي كُلِّ إِطَارٍ، تَبْنِي المُحَرِّكَاتُ الحَدِيثَةُ حَقْلَ مَسَافَةٍ مُوَقَّعًا، حَيْثُ يَحْفَظُ كُلُّ عُنْصُرٍ المَسَافَةَ إِلَى أَقْرَبِ حَافَّةٍ، فَيُعِيدُ المُظَلِّلُ بِنَاءَ الحَافَّةِ الحَادَّةِ عِنْدَ أَيِّ مِقْيَاسٍ. وَالتَّشْكِيلُ هُوَ النِّصْفُ الآخَرُ مِنَ المُشْكِلَةِ، فَسِلْسِلَةُ النِّقَاطِ البَرْمَجِيَّةِ لَيْسَتْ سِلْسِلَةَ مَحَارِفَ؛ إِذِ الرَّوَابِطُ تَنْصَهِرُ، وَالعَلَامَاتُ تَتَرَاكَبُ، وَالخُطُوطُ المُتَّصِلَةُ تَطْلُبُ أَشْكَالًا سِيَاقِيَّةً تَعْتَمِدُ عَلَى الجِوَارِ. لِذَلِكَ يَفْصِلُ مُحَرِّكُ النَّصِّ المَهَامَّ: التَّقْطِيعُ يُجَزِّئُ الفِقْرَةَ إِلَى مَقَاطِعَ ذَاتِ خَطٍّ وَاتِّجَاهٍ وَاحِدٍ، وَخُوَارِزْمِيَّةُ الاِتِّجَاهَيْنِ تُرَتِّبُ تِلْكَ المَقَاطِعَ لِلعَرْضِ، وَالمُشَكِّلُ يُحَوِّلُ العَنَاقِيدَ إِلَى مَحَارِفَ مَوْضُوعَةٍ، وَمُحَرِّكُ التَّخْطِيطِ يَكْسِرُ الأَسْطُرَ وَيُوَزِّعُ المَسَافَاتِ وَيُحَاذِي النَّتِيجَةَ دَاخِلَ صُنْدُوقِهَا. كُلُّ مَرْحَلَةٍ تَرْغَبُ فِي الحَجْزِ، وَالحَجْزُ عَدُوُّ الإِطَارِ النَّاعِمِ، فَالمَسَارُ المُنَضَبِطُ يُعِيدُ اِسْتِخْدَامَ ذَاكِرَتِهِ وَيَقِيسُ مَا يُهْدِرُهُ فِي كُلِّ عَمَلِيَّةٍ. وَالقِيَاسَاتُ تُوجَدُ لِتَجْعَلَ هَذِهِ التَّكَالِيفَ الخَفِيَّةَ مَرْئِيَّةً، فَنُنْشِئُ مِئَةَ كَائِنٍ وَنُسَخِّنُ المَخَازِنَ ثُمَّ نَفْرِضُ إِعَادَةَ بِنَاءٍ مُتَزَامِنَةً وَنَقِيسُهَا بِسَاعَةِ إِيقَافٍ. وَالغَايَةُ لَيْسَتْ رِبْحَ مُخَطَّطٍ بَلْ فَهْمَ أَيْنَ يَذْهَبُ الوَقْتُ، لِيَكُونَ التَّحْسِينُ القَادِمُ مُصَوَّبًا نَحْوَ المَرْحَلَةِ المُهَيْمِنَةِ فِعْلًا لَا المَرْئِيَّةِ فَقَطْ. وَالنَّصُّ الجَيِّدُ عَمَلٌ هَادِئٌ لَا يُلْحَظُ إِلَّا حِينَ يَغِيبُ.";

        // ~2300 chars of original Hebrew prose for shaping/BiDi exercise.
        public const string Hebrew =
            "הַדְפָּסַת טֶקְסְט עַל מְעַבֵּד גְּרָפִי הִיא מֶחְקָר שֶׁל פְּשָׁרוֹת. הָאוֹת הִיא בְּרֵאשִׁיתָהּ מִתְאָר, אוֹסֶף שֶׁל עֲקֻמּוֹת הַנִּלְפָּפוֹת כָּךְ שֶׁכְּלַל הַמִּלּוּי יַכְרִיעַ מַה בִּפְנִים הָאוֹת וּמַה מִחוּצָה לָהּ. כְּדֵי לְצַיֵּר אֶת הַמִּתְאָר בְּכָל גֹּדֶל בְּלִי לְיַצֵּר אוֹתוֹ מֵחָדָשׁ בְּכָל מִסְגֶּרֶת, מָנוֹעִים מוֹדֶרְנִיִּים בּוֹנִים שְׂדֵה מֶרְחָק חָתוּם, שֶׁבּוֹ כָּל תָּא שׁוֹמֵר אֶת הַמֶּרְחָק אֶל הַקָּצֶה הַקָּרוֹב בְּיוֹתֵר, וְהַמַּצְלֵל מְשַׁחְזֵר קַו חַד בְּכָל קְנֵה מִדָּה. הָעִצּוּב הוּא הַחֵצִי הַשֵּׁנִי שֶׁל הַבְּעָיָה, שֶׁכֵּן רְצֶף נְקֻדּוֹת הַקּוֹד אֵינוֹ רְצֶף סִימָנִים; הַקִּשּׁוּרִים נִתָּכִים, הַסִּימָנִים נֶעֱרָמִים, וְהַכְּתָבִים הַמְחֻבָּרִים דּוֹרְשִׁים צוּרוֹת הֶקְשֵׁרִיּוֹת הַתְּלוּיוֹת בַּשְּׁכֵנִים. לָכֵן מָנוֹעַ הַטֶּקְסְט מַפְרִיד בֵּין הַמְּשִׂימוֹת: הַפִּלּוּחַ מְחַלֵּק אֶת הַפִּסְקָה לְקִטְעֵי כְּתָב וְכִוּוּן אֶחָד, אַלְגוֹרִיתְם הַדּוּ־כִּוּוּנִיּוּת מְסַדֵּר אֶת הַקְּטָעִים לַתְּצוּגָה, הַמְּעַצֵּב מְמַפֶּה אֶשְׁכּוֹלוֹת לְסִימָנִים מְמֻקָּמִים, וּמָנוֹעַ הַפְּרִיסָה שׁוֹבֵר שׁוּרוֹת וּמְחַלֵּק רֶוַח וּמְיַשֵּׁר אֶת הַתּוֹצָאָה בְּתוֹךְ תֵּבָתָהּ. כָּל שָׁלָב רוֹצֶה לְהַקְצוֹת זִכָּרוֹן, וְהַהַקְצָאָה הִיא אוֹיֶבֶת הַמִּסְגֶּרֶת הַחֲלָקָה, וְלָכֵן הַנָּתִיב הַמְּמֻשְׁמָע מְמַחְזֵר אֶת הַחוֹצְצִים שֶׁלּוֹ וּמוֹדֵד אֶת מַה שֶׁהוּא מְבַזְבֵּז בְּכָל פְּעֻלָּה. הַמְּדִידוֹת קַיָּמוֹת כְּדֵי לַהֲפוֹךְ אֶת הָעֲלֻיּוֹת הַנִּסְתָּרוֹת לִגְלוּיוֹת, וְלָכֵן אָנוּ יוֹצְרִים מֵאָה עֲצָמִים, מְחַמְּמִים אֶת הַמַּטְמוֹנִים, כּוֹפִים בְּנִיָּה מְסֻנְכְּרֶנֶת וּמוֹדְדִים אוֹתָהּ בְּשָׁעוֹן עֶצֶר. הַמַּטָּרָה אֵינָהּ לְנַצֵּחַ טַבְלָה אֶלָּא לְהָבִין לְאָן הוֹלֵךְ הַזְּמַן, כְּדֵי שֶׁהַשִּׁפּוּר הַבָּא יְכֻוַּן אֶל הַשָּׁלָב הַשּׁוֹלֵט בֶּאֱמֶת. טֶקְסְט טוֹב הוּא עֲבוֹדָה שְׁקֵטָה.";

        // Mixed: interleave all three scripts + digits for BiDi + script-run stress.
        // Built at static-init to reach ~2300 chars deterministically.
        public static readonly string Mixed = BuildMixed();

        private static string BuildMixed()
        {
            // Original interleaved paragraph; repeat the unit until ~2300 chars.
            const string unit =
                "The engine shapes العربية and עברית in one pass: run 123 glyphs, then מערך 456 and مصفوفة 789 flow right-to-left while the Latin clause flows left-to-right. ";
            var sb = new System.Text.StringBuilder(2400);
            while (sb.Length < 2300)
                sb.Append(unit);
            return sb.ToString(0, System.Math.Min(sb.Length, 2300));
        }

        public static string Get(TextSetKind kind) => kind switch
        {
            TextSetKind.Latin => Latin,
            TextSetKind.Arabic => Arabic,
            TextSetKind.Hebrew => Hebrew,
            TextSetKind.Mixed => Mixed,
            _ => Latin
        };

        public static IEnumerable<TextSetKind> All()
        {
            yield return TextSetKind.Latin;
            yield return TextSetKind.Arabic;
            yield return TextSetKind.Hebrew;
            yield return TextSetKind.Mixed;
        }
    }
}
