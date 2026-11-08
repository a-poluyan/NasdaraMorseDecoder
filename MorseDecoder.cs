namespace NasdaraMorseDecoder;

internal record Bunker(string Grid, string Code, DateTimeOffset Received);

internal sealed class MorseDecoder(int toneHz, Action<Bunker> found)
{
    private static readonly string[] Digits =
        ["-----", ".----", "..---", "...--", "....-", ".....", "-....", "--...", "---..", "----."];
    private const double MessageGap = 4.5;
    private readonly char[] elements = new char[5];
    private readonly char[] digits = new char[10];
    private int elementCount, digitCount, lockedHz;
    private double lastEnd = double.NaN;
    private double pulseStart;
    private char lastSymbol;
    private bool inPulse, synchronized, hasPrediction;
    private double[] coefficients = [];
    private int sampleRate;
    private double noisePower = 1e-7;
    private double? transitionStart;
    private double lastMorsePulse = double.NegativeInfinity;
    private int lastMorseTone;

    public double Level { get; private set; }
    public string PartialDigits => new(digits, 0, digitCount + (hasPrediction ? 1 : 0));
    public DateTimeOffset? LastDigitReceivedAt { get; private set; }
    public bool Receiving => synchronized && (elementCount != 0 || digitCount != 0);
    public int DetectedToneHz => lastMorseTone;
    public int RecentTone(double time) => time - lastMorsePulse <= 3 ? lastMorseTone : 0;

    public void ProcessFrame(ReadOnlySpan<float> pcm, int rate, double time)
    {
        if (rate != sampleRate)
        {
            sampleRate = rate;
            var frequencies = toneHz == 0 ? Enumerable.Range(0, 139).Select(i => 240 + i * 20) :
                Enumerable.Range(-2, 5).Select(i => toneHz + i * 20);
            coefficients = frequencies.Select(hz => 2 * Math.Cos(2 * Math.PI * hz / rate)).ToArray();
        }

        double total = 0;
        foreach (float sample in pcm)
            total += sample * sample;
        Level = Math.Sqrt(total / pcm.Length);

        int bestHz = 0;
        double bestPower = 0;
        int firstHz = toneHz == 0 ? 240 : toneHz - 40;
        for (int i = 0; i < coefficients.Length; i++)
        {
            int hz = firstHz + i * 20;
            if (lockedHz != 0 && Math.Abs(hz - lockedHz) > 40)
                continue;
            double previous = 0, beforePrevious = 0;
            double coefficient = coefficients[i];
            foreach (float sample in pcm)
            {
                double next = sample + coefficient * previous - beforePrevious;
                beforePrevious = previous;
                previous = next;
            }
            double power = (previous * previous + beforePrevious * beforePrevious -
                coefficient * previous * beforePrevious) * 2 / ((double)pcm.Length * pcm.Length);
            if (power > bestPower)
            {
                bestPower = power;
                bestHz = hz;
            }
        }

        double ratio = total > 0 ? bestPower / (total / pcm.Length) : 0;
        bool audible = bestPower >= Math.Max(1e-7, noisePower * (inPulse ? 2 : 4)) &&
            ratio >= (inPulse ? 0.20 : 0.35);
        if (!audible)
            noisePower += 0.025 * (Math.Min(bestPower, 1e-5) - noisePower);
        if (audible == inPulse)
        {
            transitionStart = null;
            Observe(audible ? bestHz : 0, time);
        }
        else if (transitionStart is double start)
        {
            transitionStart = null;
            Observe(audible ? bestHz : 0, start);
        }
        else
        {
            transitionStart = time;
        }
    }

    public void ObserveSilence(double time)
    {
        Level = 0;
        Observe(0, inPulse ? transitionStart ?? time : time);
        transitionStart = null;
        AdvancePause(time);
    }

    private void Observe(int hz, double time)
    {
        if (double.IsNaN(lastEnd))
            lastEnd = time;

        if (hz != 0)
        {
            if (inPulse)
                return;
            AdvancePause(time);
            double gap = time - lastEnd;
            if (synchronized && (elementCount != 0 || digitCount != 0))
            {
                // The client plays a short sound, then waits out the rest of the Morse slot.
                double expected = lastSymbol == '.' ? 0.8 : 1.425;
                double digitGap = elementCount == 0 ? 0.4 : 0;
                // The coordinate/PIN boundary can omit the additional digit gap.
                bool codeBoundary = elementCount == 0 && digitCount == 6;
                if (Math.Abs(gap - expected - digitGap) > 0.32 &&
                    !(codeBoundary && Math.Abs(gap - expected) <= 0.32))
                    Reject();
            }
            lockedHz = hz;
            pulseStart = time;
            inPulse = true;
            return;
        }

        if (inPulse)
        {
            inPulse = false;
            lastEnd = time;
            double duration = time - pulseStart;
            char symbol = duration switch
            {
                >= 0.045 and <= 0.22 => '.',
                >= 0.32 and <= 0.68 => '-',
                _ => '\0'
            };
            if (symbol != '\0')
            {
                lastMorsePulse = time;
                lastMorseTone = lockedHz;
            }
            if (synchronized)
                AddElement(symbol);
            else
                lockedHz = 0;
        }
        AdvancePause(time);
    }

    private void AddElement(char symbol)
    {
        if (symbol == '\0' || digitCount == digits.Length)
        {
            Reject();
            return;
        }
        lastSymbol = symbol;
        elements[elementCount++] = symbol;
        var prefix = elements.AsSpan(0, elementCount);
        int digit = -1;
        for (int candidate = 0; candidate < Digits.Length; candidate++)
        {
            if (!Digits[candidate].AsSpan(0, elementCount).SequenceEqual(prefix))
                continue;
            if (digit >= 0)
                return;
            digit = candidate;
        }
        if (digit < 0)
        {
            Reject();
            return;
        }
        digits[digitCount] = (char)('0' + digit);
        hasPrediction = elementCount < elements.Length;
        if (hasPrediction)
            return;

        digitCount++;
        elementCount = 0;
        if (digitCount == digits.Length)
            LastDigitReceivedAt = DateTimeOffset.Now;
    }

    private void AdvancePause(double time)
    {
        double gap = time - lastEnd;
        if (gap >= MessageGap)
        {
            if (synchronized && LastDigitReceivedAt is DateTimeOffset received)
                found(new Bunker(new string(digits, 0, 3) + " " + new string(digits, 3, 3),
                    new string(digits, 6, 4), received));
            elementCount = digitCount = lockedHz = 0;
            hasPrediction = false;
            LastDigitReceivedAt = null;
            synchronized = true;
        }
    }

    private void Reject()
    {
        synchronized = false;
        elementCount = digitCount = lockedHz = 0;
        hasPrediction = false;
        LastDigitReceivedAt = null;
    }
}

internal sealed class PcmFrames(int rate, MorseDecoder decoder)
{
    private readonly float[] frame = new float[rate / 40];
    private int count;
    private long frameStart;

    public void Push(float sample, long position)
    {
        if (count == 0)
            frameStart = position;
        frame[count++] = sample;
        if (count != frame.Length)
            return;
        decoder.ProcessFrame(frame, rate, (double)frameStart / rate);
        count = 0;
    }

    public void Silence(long position)
    {
        if (count > 0)
        {
            Array.Clear(frame, count, frame.Length - count);
            decoder.ProcessFrame(frame, rate, (double)frameStart / rate);
            count = 0;
        }
        decoder.ObserveSilence((double)position / rate);
    }
}
