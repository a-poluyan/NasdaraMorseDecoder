using NasdaraMorseDecoder;

if (args.Contains("--loopback"))
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    AudioStatus? latest = null;
    Loopback.Listen(0, status =>
    {
        latest = status;
        if (status.Device.Length > 0)
            stop.Cancel();
    }, stop.Token);
    if (latest?.Device is not { Length: > 0 })
    {
        Console.Error.WriteLine(latest?.State ?? "Нет данных от WASAPI.");
        return 1;
    }
    Console.WriteLine($"PASS: WASAPI loopback started on {latest.Device}");
    return 0;
}

CheckSeries(48000, 987, 980);
CheckSeries(44100, 1012, 0);
Console.WriteLine("PASS: live digits, leading zeros, continuous messages, repeats, resync and noise.");
return 0;

static void CheckSeries(int rate, int hz, int setting)
{
    var decoded = new List<Bunker>();
    var decoder = new MorseDecoder(setting, decoded.Add);
    var frames = new PcmFrames(rate, decoder);
    int[] uniqueAfter = [5, 2, 3, 4, 5, 5, 2, 3, 4, 5];
    DateTimeOffset? previewTimestamp = null;
    var random = new Random(19);
    long sample = 0;

    void Play(double seconds, bool tone = false)
    {
        long end = sample + (long)(rate * seconds);
        for (; sample < end; sample++)
        {
            double noise = (random.NextDouble() - 0.5) * 0.045;
            double signal = tone ? 0.10 * Math.Sin(2 * Math.PI * hz * sample / rate) : 0;
            frames.Push((float)(signal + noise), sample);
        }
    }

    void Message(string value, bool checkPreview = false)
    {
        for (int digit = 0; digit < value.Length; digit++)
        {
            int number = value[digit] - '0';
            string morse = number <= 5 ? new string('.', number) + new string('-', 5 - number) :
                new string('-', number - 5) + new string('.', 10 - number);
            for (int i = 0; i < morse.Length; i++)
            {
                bool dot = morse[i] == '.';
                Play(dot ? 0.085 : 0.475, true);
                double gap = dot ? 0.815 : 1.425;
                if (i == 4)
                    gap = digit == value.Length - 1 ? 7.4 : gap + (digit == 5 ? 0 : 0.4);
                if (checkPreview)
                {
                    Play(0.1);
                    int visible = digit + (i + 1 >= uniqueAfter[number] ? 1 : 0);
                    Require(decoder.PartialDigits == value[..visible], "digit appears at its first unique prefix");
                    Require(Math.Abs(decoder.DetectedToneHz - hz) <= 20, "detected tone matches audio");
                    previewTimestamp = decoder.LastDigitReceivedAt;
                    Require(previewTimestamp.HasValue == (digit == 9 && i == 4), "timestamp only after confirmation");
                    gap -= 0.1;
                }
                Play(gap);
            }
        }
    }

    void Require(bool condition, string description)
    {
        if (!condition)
            throw new Exception($"{rate} Hz, tone {hz}: {description}");
    }

    Message("660074"); // Start in the middle of a message.
    Require(decoded.Count == 0, "partial initial message");
    Message("0390660074", checkPreview: true);
    Require(decoded[0].Received == previewTimestamp, "accepted row preserves the final digit timestamp");
    Message("1140538629", checkPreview: true);
    Message("0180953156");
    Require(decoded.Count == 3, "three consecutive messages");
    Require(decoded.Select(b => (b.Grid, b.Code)).SequenceEqual(new[]
    {
        ("039 066", "0074"), ("114 053", "8629"), ("018 095", "3156")
    }), "coordinates and codes match");

    Message("0390660074");
    Require(decoded.Count == 4 && decoded[3].Code == "0074", "repeat recorded after third message");
    Play(6);
    Require(decoder.PartialDigits.Length == 0, "preview clears after acceptance");
    Message("039066007"); // Missing one digit.
    Require(decoded.Count == 4, "corrupt message ignored");
    Message("0390660075");
    Require(decoded.Count == 5 && decoded[4].Code == "0075", "recovery and new code");
    Play(6);
    Message("0180953516");
    Require(decoded.Count == 6 && decoded[5].Code == "3516", "reception continues after a long pause");
    Play(15);
    Require(decoded.Count == 6, "noise does not emit messages");
    Play(0.085, true);
    Play(0.815);
    Play(0.475, true);
    Play(1.425);
    Require(decoder.PartialDigits == "1", "dot-dash predicts one");
    Play(0.085, true);
    Play(0.1);
    Require(decoder.PartialDigits == "" && decoded.Count == 6, "invalid continuation clears prediction");
}
