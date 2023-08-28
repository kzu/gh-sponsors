﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Devlooped;

/// <summary>
/// Contains the logic for creating, reading and checking the status 
/// of the sponsors manifest, entirely offline.
/// </summary>
public static partial class SponsorLink
{
    static readonly SHA256 sha = SHA256.Create();
    static readonly Manifest? manifest;

    static SponsorLink()
    {
        PublicKey = CreateRSAFromPublicKey(Convert.FromBase64String(Constants.PublicKey));
        Status = Manifest.TryRead(out manifest);
    }

    /// <summary>
    /// The public key used to validate manifests signed with the default private key.
    /// </summary>
    public static RSA PublicKey { get; }

    /// <summary>
    /// Status of the sponsors manifest.
    /// </summary>
    public static ManifestStatus Status { get; } = ManifestStatus.NotFound;

    /// <summary>
    /// Checks whether the given user (represented somehow by a string, might 
    /// be a user identifier an email, etc.) is sponsoring the given sponsorable account.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> if sponsoring status cannot be determined (i.e. 
    /// <see cref="Status"/> is different than <see cref="ManifestStatus.Verified"/>). 
    /// Otherwise, <see langword="true"/> if the email is sponsoring the sponsorable 
    /// account, or <see langword="false"/> otherwise.
    /// </returns>
    public static bool? IsSponsoring(string user, string sponsorable)
        => manifest?.IsSponsoring(user, sponsorable);

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

    /// <summary>
    /// Status of the sponsors manifest.
    /// </summary>
    public enum ManifestStatus
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

    /// <summary>
    /// Environment variable names used by the SponsorLink CLI.
    /// </summary>
    public class Constants
    {
        /// <summary>
        /// Base64-encoded public key used to verify the manifest JWT token.
        /// </summary>
        public const string PublicKey = "MIIBCgKCAQEAo5bLk9Iim5twrxGVzJ4OxfxDuvy3ladTNvcFNv4Hm9/1No89SISKTXZ1bSABTnqH6z/DpklcHveGMSmsncEvUebrg7tX6+M3byVXU6Q/d82PtwgbDXT9d10A4lePS2ioJQqlHWQy/fuNwe7FjptV+yguf5IUxVRdZ77An1IyGUk9Cj6n4RuYIPrP5O0AmFPHOwEzywUWVaV1NHYRe0Th6i5/hyDV13K7+LP9VzwucnWEvzujtnL6ywZDeaKkwfeFsXZyYywHj6oJK9Obed/nu1e+69fmUqprtc0t/3A9uHc0G/0sDNLLAd83j2NSOS2IHJo17azOLFuhekka8dSKnQIDAQAB";

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
        readonly string salt;
        readonly HashSet<string> hashes;

        Manifest(string jwt, string salt, ClaimsPrincipal principal)
            : this(jwt, salt, new HashSet<string>(principal.FindAll("hash").Select(x => x.Value))) { }

        Manifest(string jwt, string salt, HashSet<string> hashes)
            => (Token, this.salt, this.hashes) = (jwt, salt, hashes);

        /// <summary>
        /// Checks whether the given user (represented somehow by a string, might 
        /// be a user identifier an email, etc.) is sponsoring the given sponsorable account.
        /// </summary>
        public bool IsSponsoring(string user, string sponsorable)
            => hashes.Contains(
                    Convert.ToBase64String(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(salt + user + sponsorable)))) ||
                // If the user string contains an @ sign, we can test for the domain for org-wide sponsorships.
                (user.IndexOf('@') is int index && index > 0 &&
                 hashes.Contains(
                    Convert.ToBase64String(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(salt + user[(index + 1)..] + sponsorable)))));

        /// <summary>
        /// Gets the expiration date of the current manifest.
        /// </summary>
        public DateTime ExpiresAt { get; private set; }

        /// <summary>
        /// Hashes contained in the manifest.
        /// </summary>
        public HashSet<string> Hashes => hashes;

        /// <summary>
        /// The JWT token representing the manifest.
        /// </summary>
        public string Token { get; }

        /// <summary>
        /// Tries to read the default manifest.
        /// </summary>
        /// <param name="manifest">The read manifest if present and valid.</param>
        /// <returns>The manifest status.</returns>
        public static ManifestStatus TryRead(out Manifest? manifest)
        {
            manifest = default;

            var jwt = Variables.Manifest;
            var salt = Variables.InstallationId;

            // We need both values in order to use the manifest at all. 
            // These are generated by the gh sponsors sync command.
            if (jwt is null || salt is null)
                return ManifestStatus.NotFound;

            try
            {
                manifest = Read(jwt, salt);
                return ManifestStatus.Verified;
            }
            catch (SecurityTokenExpiredException)
            {
                return ManifestStatus.Expired;
            }
            catch (SecurityTokenException)
            {
                return ManifestStatus.Invalid;
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
}