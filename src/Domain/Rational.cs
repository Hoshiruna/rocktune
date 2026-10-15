// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace RockTune.Domain;

/// <summary>Exact timestamps keep rounding at absolute frame boundaries.</summary>
public readonly struct Rational : IComparable<Rational>, IEquatable<Rational>
{
    public BigInteger Numerator { get; }
    private readonly BigInteger denominator;
    public BigInteger Denominator => denominator.IsZero ? BigInteger.One : denominator;

    public Rational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new MusicException("A fraction cannot have a zero denominator");
        }
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        this.denominator = denominator / divisor;
    }

    public static Rational Parse(string text)
    {
        text = text.Trim();
        var fraction = Regex.Match(text, @"^([+-]?\d+)\s*/\s*([+-]?\d+)$");
        if (fraction.Success)
        {
            return new Rational(BigInteger.Parse(fraction.Groups[1].Value, CultureInfo.InvariantCulture),
                BigInteger.Parse(fraction.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        var number = Regex.Match(text, @"^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$");
        if (!number.Success || number.Groups[2].Length + number.Groups[3].Length == 0)
        {
            throw new MusicException("Expected a number or fraction, got " + text);
        }
        var digits = BigInteger.Parse("0" + number.Groups[2].Value + number.Groups[3].Value,
            CultureInfo.InvariantCulture);
        if (number.Groups[1].Value == "-")
        {
            digits = -digits;
        }
        var exponent = number.Groups[4].Success
            ? int.Parse(number.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        var scale = checked(number.Groups[3].Length - exponent);
        return scale >= 0
            ? new Rational(digits, BigInteger.Pow(10, scale))
            : new Rational(digits * BigInteger.Pow(10, checked(-scale)), 1);
    }

    public int CompareTo(Rational other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    public bool Equals(Rational other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is Rational value && Equals(value);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public double ToDouble() => (double)Numerator / (double)Denominator;
    public override string ToString() => Denominator.IsOne
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");
    public object TickValue() => Denominator.IsOne ? (object)checked((long)Numerator) : ToString();

    public static implicit operator Rational(int value) => new(value, 1);
    public static implicit operator Rational(long value) => new(value, 1);
    public static Rational operator +(Rational a, Rational b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static Rational operator -(Rational a, Rational b) => new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static Rational operator -(Rational a) => new(-a.Numerator, a.Denominator);
    public static Rational operator *(Rational a, Rational b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
    public static Rational operator /(Rational a, Rational b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
    public static bool operator ==(Rational a, Rational b) => a.Equals(b);
    public static bool operator !=(Rational a, Rational b) => !a.Equals(b);
    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;
    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;
    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;
    public static Rational Min(Rational a, Rational b) => a <= b ? a : b;
    public static Rational Max(Rational a, Rational b) => a >= b ? a : b;

    public static int FrameAt(Rational time, Rational fps)
    {
        var value = time * fps;
        var top = 2 * value.Numerator + value.Denominator;
        var bottom = 2 * value.Denominator;
        var result = BigInteger.DivRem(top, bottom, out var remainder);
        if (remainder.Sign < 0)
        {
            result--;
        }
        return checked((int)result);
    }
}
