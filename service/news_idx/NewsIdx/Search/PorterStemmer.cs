namespace NewsIdx.Search;

/// <summary>
/// Martin Porter's 1980 English stemmer, ported from his reference C implementation. "fishing", "fished" and
/// "fishes" all become "fish", so the BM25 index matches word forms, not only exact spellings.
/// Input must be lower-case a–z; anything else is returned unchanged.
/// </summary>
internal sealed class PorterStemmer
{
    private readonly char[] _b;
    private int _k;
    private int _j;

    private PorterStemmer(string word)
    {
        _b = word.ToCharArray();
        _k = _b.Length - 1;
    }

    public static string Stem(string word)
    {
        if (word.Length <= 2)
        {
            return word;
        }
        foreach (char c in word)
        {
            if (c is < 'a' or > 'z')
            {
                return word;
            }
        }

        var s = new PorterStemmer(word);
        s.Step1Ab();
        if (s._k > 0)
        {
            s.Step1C();
            s.Step2();
            s.Step3();
            s.Step4();
            s.Step5();
        }
        return new string(s._b, 0, s._k + 1);
    }

    private bool Cons(int i) => _b[i] switch
    {
        'a' or 'e' or 'i' or 'o' or 'u' => false,
        'y' => i == 0 || !Cons(i - 1),
        _ => true,
    };

    /// <summary>The number of consonant-vowel sequences in b[0..j].</summary>
    private int M()
    {
        int n = 0;
        int i = 0;
        while (true)
        {
            if (i > _j) return n;
            if (!Cons(i)) break;
            i++;
        }
        i++;
        while (true)
        {
            while (true)
            {
                if (i > _j) return n;
                if (Cons(i)) break;
                i++;
            }
            i++;
            n++;
            while (true)
            {
                if (i > _j) return n;
                if (!Cons(i)) break;
                i++;
            }
            i++;
        }
    }

    private bool VowelInStem()
    {
        for (int i = 0; i <= _j; i++)
        {
            if (!Cons(i)) return true;
        }
        return false;
    }

    private bool DoubleC(int j) => j >= 1 && _b[j] == _b[j - 1] && Cons(j);

    private bool Cvc(int i)
    {
        if (i < 2 || !Cons(i) || Cons(i - 1) || !Cons(i - 2)) return false;
        return _b[i] is not ('w' or 'x' or 'y');
    }

    private bool Ends(string s)
    {
        int length = s.Length;
        int offset = _k - length + 1;
        if (offset < 0) return false;
        for (int i = 0; i < length; i++)
        {
            if (_b[offset + i] != s[i]) return false;
        }
        _j = _k - length;
        return true;
    }

    private void SetTo(string s)
    {
        int offset = _j + 1;
        for (int i = 0; i < s.Length; i++)
        {
            _b[offset + i] = s[i];
        }
        _k = _j + s.Length;
    }

    private void R(string s)
    {
        if (M() > 0) SetTo(s);
    }

    /// <summary>Plurals and -ed / -ing.</summary>
    private void Step1Ab()
    {
        if (_b[_k] == 's')
        {
            if (Ends("sses")) _k -= 2;
            else if (Ends("ies")) SetTo("i");
            else if (_b[_k - 1] != 's') _k--;
        }
        if (Ends("eed"))
        {
            if (M() > 0) _k--;
        }
        else if ((Ends("ed") || Ends("ing")) && VowelInStem())
        {
            _k = _j;
            if (Ends("at")) SetTo("ate");
            else if (Ends("bl")) SetTo("ble");
            else if (Ends("iz")) SetTo("ize");
            else if (DoubleC(_k))
            {
                _k--;
                if (_b[_k] is 'l' or 's' or 'z') _k++;
            }
            else if (M() == 1 && Cvc(_k))
            {
                _j = _k;
                SetTo("e");
            }
        }
    }

    /// <summary>Terminal y → i when there is another vowel in the stem.</summary>
    private void Step1C()
    {
        if (Ends("y") && VowelInStem()) _b[_k] = 'i';
    }

    /// <summary>Double suffixes → single ones (-ization → -ize).</summary>
    private void Step2()
    {
        if (_k < 1) return;
        foreach ((string from, string to) in _b[_k - 1] switch
                 {
                     'a' => [("ational", "ate"), ("tional", "tion")],
                     'c' => [("enci", "ence"), ("anci", "ance")],
                     'e' => [("izer", "ize")],
                     'l' => [("bli", "ble"), ("alli", "al"), ("entli", "ent"), ("eli", "e"), ("ousli", "ous")],
                     'o' => [("ization", "ize"), ("ation", "ate"), ("ator", "ate")],
                     's' => [("alism", "al"), ("iveness", "ive"), ("fulness", "ful"), ("ousness", "ous")],
                     't' => [("aliti", "al"), ("iviti", "ive"), ("biliti", "ble")],
                     'g' => [("logi", "log")],
                     _ => Array.Empty<(string, string)>(),
                 })
        {
            if (Ends(from))
            {
                R(to);
                return;
            }
        }
    }

    /// <summary>-ic-, -full, -ness etc.</summary>
    private void Step3()
    {
        foreach ((string from, string to) in _b[_k] switch
                 {
                     'e' => [("icate", "ic"), ("ative", ""), ("alize", "al")],
                     'i' => [("iciti", "ic")],
                     'l' => [("ical", "ic"), ("ful", "")],
                     's' => [("ness", "")],
                     _ => Array.Empty<(string, string)>(),
                 })
        {
            if (Ends(from))
            {
                R(to);
                return;
            }
        }
    }

    /// <summary>-ant, -ence etc. in context &lt;c&gt;vcvc&lt;v&gt;.</summary>
    private void Step4()
    {
        if (_k < 1) return;
        string[] suffixes = _b[_k - 1] switch
        {
            'a' => ["al"],
            'c' => ["ance", "ence"],
            'e' => ["er"],
            'i' => ["ic"],
            'l' => ["able", "ible"],
            'n' => ["ant", "ement", "ment", "ent"],
            'o' => ["ion", "ou"],
            's' => ["ism"],
            't' => ["ate", "iti"],
            'u' => ["ous"],
            'v' => ["ive"],
            'z' => ["ize"],
            _ => [],
        };
        foreach (string suffix in suffixes)
        {
            if (!Ends(suffix)) continue;
            if (suffix == "ion" && (_j < 0 || _b[_j] is not ('s' or 't'))) continue;
            if (M() > 1) _k = _j;
            return;
        }
    }

    /// <summary>Final -e and -ll.</summary>
    private void Step5()
    {
        _j = _k;
        if (_b[_k] == 'e')
        {
            int a = M();
            if (a > 1 || (a == 1 && !Cvc(_k - 1))) _k--;
        }
        if (_b[_k] == 'l' && DoubleC(_k) && M() > 1) _k--;
    }
}
