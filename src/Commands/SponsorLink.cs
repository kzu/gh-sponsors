﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Devlooped.SponsorLink;

/// <summary>
/// Environment variable names used by the SponsorLink CLI.
/// </summary>
public class Constants
{
    /// <summary>
    /// Last used access token to invoke SponsorLink backend APIs.
    /// </summary>
    public const string AccessTokenVariable = "SPONSORLINK_TOKEN";

    /// <summary>
    /// Random GUID used for salting hashes. Unique per installation, can be 
    /// regenerated by deleting the environment variable.
    /// </summary>
    public const string InstallationIdVariable = "SPONSORLINK_INSTALLATION";

    /// <summary>
    /// JWT token containing the hashed claims that represent active 
    /// sponsorships.
    /// </summary>
    public const string ManifestVariable = "SPONSORLINK_MANIFEST";

    /// <summary>
    /// Default audience for the manifest token.
    /// </summary>
    public const string ManifestAudience = "SponsorLink";

    /// <summary>
    /// Default issuer for the manifest token.
    /// </summary>
    public const string ManifestIssuer = "Devlooped";
}

/// <summary>
/// Provides simplified access to the key environment variables.
/// </summary>
public static partial class Variables
{
    /// <summary>
    /// Last used access token to invoke SponsorLink backend APIs.
    /// </summary>
    public static string? AccessToken
    {
        get => Environment.GetEnvironmentVariable(Constants.AccessTokenVariable, EnvironmentVariableTarget.User) is string token ?
            !string.IsNullOrEmpty(token) ?
            token : null : null;
        set => Environment.SetEnvironmentVariable(Constants.AccessTokenVariable, value, EnvironmentVariableTarget.User);
    }

    /// <summary>
    /// Random GUID used for salting hashes. Unique per installation, can be 
    /// regenerated by deleting the environment variable.
    /// </summary>
    public static string? InstallationId
    {
        get => Environment.GetEnvironmentVariable(Constants.InstallationIdVariable, EnvironmentVariableTarget.User) is string id ?
            !string.IsNullOrEmpty(id) ?
            id : null : null;
        set => Environment.SetEnvironmentVariable(Constants.InstallationIdVariable, value, EnvironmentVariableTarget.User);
    }

    /// <summary>
    /// Gets the JWT token containing the hashed claims that represent 
    /// active sponsorships.
    /// </summary>
    public static string? Manifest
    {
        get => Environment.GetEnvironmentVariable(Constants.ManifestVariable, EnvironmentVariableTarget.User) is string manifest ?
            !string.IsNullOrEmpty(manifest) ?
            manifest : null : null;
        set => Environment.SetEnvironmentVariable(Constants.ManifestVariable, value, EnvironmentVariableTarget.User);
    }
}

/// <summary>
/// Represents a manifest of sponsorship claims.
/// </summary>
public class Manifest
{
    static readonly SHA256 sha = SHA256.Create();
    readonly string salt;
    readonly HashSet<string> linked;

    /// <summary>
    /// Status of a manifest.
    /// </summary>
    public enum Status
    {
        /// <summary>
        /// The manifest is expired and needs re-issuing via <c>gh sponsors sync</c>.
        /// </summary>
        Expired,
        /// <summary>
        /// The manifest is invalid and needs re-issuing via <c>gh sponsors sync</c>.
        /// </summary>
        Invalid,
        /// <summary>
        /// The manifest was not found at all. Requires running <c>gh sponsors sync</c>.
        /// </summary>
        NotFound,
        /// <summary>
        /// The manifest was found and is valid.
        /// </summary>
        Verified,
    }

    static Manifest()
    {
        using var stream = typeof(Manifest).Assembly
            .GetManifestResourceStream("Devlooped.SponsorLink.SponsorLink.pub");

        var mem = new MemoryStream((int)stream!.Length);
        stream.CopyTo(mem);

        PublicKey = CreateRSAFromPublicKey(mem.ToArray());
    }

#if NET6_0_OR_GREATER
    static RSA CreateRSAFromPublicKey(byte[] publicKey)
    {
        var rsa = RSA.Create();
        rsa.ImportRSAPublicKey(publicKey, out _);
        return rsa;
    }
#else
    /// <summary>
    /// NOTE: this downlevel implementation requires a package reference to 
    /// BouncyCastle.Cryptography
    /// </summary>
    static RSA CreateRSAFromPublicKey(byte[] publicKey)
    {
        var asn1Object = Org.BouncyCastle.Asn1.Asn1Object.FromByteArray(publicKey);
        var publicKeyStructure = Org.BouncyCastle.Asn1.X509.RsaPublicKeyStructure.GetInstance(asn1Object);
        var rsaParameters = new Org.BouncyCastle.Crypto.Parameters.RsaKeyParameters(false, publicKeyStructure.Modulus, publicKeyStructure.PublicExponent);

        var rsaParams = new RSAParameters
        {
            Modulus = rsaParameters.Modulus.ToByteArrayUnsigned(),
            Exponent = rsaParameters.Exponent.ToByteArrayUnsigned()
        };

        var rsa = RSA.Create();
        rsa.ImportParameters(rsaParams);
        return rsa;
    }
#endif

    Manifest(string jwt, string salt, ClaimsPrincipal principal)
        : this(jwt, salt, new HashSet<string>(principal.FindAll("hash").Select(x => x.Value))) { }

    Manifest(string jwt, string salt, HashSet<string> linked)
        => (Token, this.salt, this.linked) = (jwt, salt, linked);

    /// <summary>
    /// The public key used to validate manifests signed with the default private key.
    /// </summary>
    public static RSA PublicKey { get; }

    /// <summary>
    /// Checks whether the given email is sponsoring the given sponsorable account.
    /// </summary>
    public bool IsSponsoring(string email, string sponsorable)
        => linked.Contains(
                Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(salt + email + sponsorable)))) ||
            (email.IndexOf('@') is int index && index > 0 &&
             linked.Contains(
                Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(salt + email[(index + 1)..] + sponsorable)))));

    /// <summary>
    /// Gets the expiration date of the current manifest.
    /// </summary>
    public DateTime ExpiresAt { get; private set; }

    /// <summary>
    /// Hashes contained in the manifest.
    /// </summary>
    public IEnumerable<string> Hashes => linked;

    /// <summary>
    /// The JWT token representing the manifest.
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// Tries to read the default manifest.
    /// </summary>
    /// <param name="manifest">The read manifest if present and valid.</param>
    /// <returns>The manifest status.</returns>
    public static Status TryRead(out Manifest? manifest)
    {
        manifest = default;

        var jwt = Variables.Manifest;
        var salt = Variables.InstallationId;

        // We need both values in order to use the manifest at all. 
        // These are generated by the gh sponsors sync command.
        if (jwt is null || salt is null)
            return Status.NotFound;

        try
        {
            manifest = Read(jwt, salt);
            return Status.Verified;
        }
        catch (SecurityTokenExpiredException)
        {
            return Status.Expired;
        }
        catch (SecurityTokenException)
        {
            return Status.Invalid;
        }
    }

    /// <summary>
    /// Reads a manifest and validates it using the embedded public key.
    /// </summary>
    public static Manifest Read(string token) => Read(token,
        Variables.InstallationId ??= Guid.NewGuid().ToString("N"), PublicKey);

    /// <summary>
    /// Reads a manifest and validates it using the embedded public key.
    /// </summary>
    public static Manifest Read(string token, string salt) => Read(token, salt, PublicKey);

    /// <summary>
    /// Reads a manifest and validates it using the given public key.
    /// </summary>
    internal static Manifest Read(string token, string salt, RSA rsa)
    {
        var validation = new TokenValidationParameters
        {
            RequireExpirationTime = true,
            ValidAudience = "SponsorLink",
            ValidIssuer = "Devlooped",
            IssuerSigningKey = new RsaSecurityKey(rsa)
        };

        var principal = new JwtSecurityTokenHandler().ValidateToken(token, validation, out var securityToken);

        return new Manifest(token, salt, principal)
        {
            ExpiresAt = securityToken.ValidTo
        };
    }

    /// <summary>
    /// Creates an unsigned manifest, to be used to request a signed one.
    /// </summary>
    /// <param name="salt">A random string used to salt the values to be hashed.</param>
    /// <param name="user">The identifier of the manifest owner.</param>
    /// <param name="emails">Email(s) of the manifest owner.</param>
    /// <param name="domains">Verified organization domains the user belongs to.</param>
    /// <param name="sponsoring">The accounts the manifest owner is sponsoring.</param>
    public static Manifest Create(string salt, string user, string[] emails, string[] domains, string[] sponsoring)
    {
        var linked = new HashSet<string>();

        foreach (var sponsorable in sponsoring)
        {
            foreach (var email in emails)
            {
                var data = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + email + sponsorable));
                var hash = Convert.ToBase64String(data);

                linked.Add(hash);
            }

            foreach (var domain in domains)
            {
                var data = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + domain + sponsorable));
                var hash = Convert.ToBase64String(data);

                linked.Add(hash);
            }
        }

        // Expire the first day of the next month
        var expiration = DateTime.UtcNow.AddMonths(1);
        expiration = new DateTime(expiration.Year, expiration.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var token = new JwtSecurityToken(
            issuer: "Devlooped",
            audience: "SponsorLink",
            claims: new[] { new Claim("sub", user) }.Concat(linked.Select(x => new Claim("hash", x))),
            expires: expiration);

        // Serialize the token and return as a string
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        return new Manifest(jwt, salt, linked)
        {
            ExpiresAt = token.ValidTo
        };
    }

    /// <summary>
    /// Signs the manifes with the given key and returns the new JWT.
    /// </summary>
    /// <param name="key">The RSA key to use for signing.</param>
    public string Sign(RSA key)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(Token);
        var signing = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256);

        var jwt = new JwtSecurityToken(
            issuer: token.Issuer,
            audience: token.Audiences.First(),
            claims: token.Claims.Where(c => c.Type != "exp" && c.Type != "aud" && c.Type != "iss"),
            expires: token.ValidTo,
            signingCredentials: signing);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}