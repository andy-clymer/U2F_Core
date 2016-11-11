﻿using System.Collections.Generic;
using U2F.Core.Models;
using U2F.Core.Utils;

namespace U2F.Core.Crypto
{
    public static class U2F
    {
        private static ICrytoService _crypto = new CryptoService();
        public const string U2FVersion = "U2F_V2";
        private const string AuthenticateTyp = "navigator.id.getAssertion";
        private const string RegisterType = "navigator.id.finishEnrollment";

        public static ICrytoService Crypto
        {
            get { return _crypto; }
            private set { _crypto = value; }
        }

        /// <summary>
        /// Initiates the registration of a device.
        /// </summary>
        /// <param name="appId">ppId the U2F AppID. Set this to the Web Origin of the login page, unless you need to support logging in from multiple Web Origins.</param>
        /// <returns>a StartedRegistration, which should be sent to the client and temporary saved by the server.</returns>
        public static StartedRegistration StartRegistration(string appId)
        {
            byte[] challenge = Crypto.GenerateChallenge();
            string challengeBase64 = challenge.ByteArrayToBase64String();
        
            return new StartedRegistration(challengeBase64, appId);
        }

        /// <summary>
        /// Finishes a previously started registration.
        /// </summary>
        /// <param name="startedRegistration">started registration response.</param>
        /// <param name="tokenResponse">tokenResponse the response from the token/client.</param>
        /// <param name="facets">A list of valid facets to verify against. (note: optional)</param>
        /// <returns>a DeviceRegistration object, holding information about the registered device. Servers should persist this.</returns>
        public static DeviceRegistration FinishRegistration(StartedRegistration startedRegistration,
                                                            RegisterResponse tokenResponse, HashSet<string> facets = null)
        {
            ClientData clientData = tokenResponse.GetClientData();
            clientData.CheckContent(RegisterType, startedRegistration.Challenge, facets);
        
            RawRegisterResponse rawRegisterResponse = RawRegisterResponse.FromBase64(tokenResponse.RegistrationData);
            rawRegisterResponse.CheckSignature(startedRegistration.AppId, clientData.AsJson());
        
            return rawRegisterResponse.CreateDevice();
        }

        /// <summary>
        /// Initiates the authentication process.
        /// </summary>
        /// <param name="appId">appId the U2F AppID. Set this to the Web Origin of the login page, unless you need to support logging in from multiple Web Origins.</param>
        /// <param name="deviceRegistration">the DeviceRegistration for which to initiate authentication.</param>
        /// <returns>a StartedAuthentication which should be sent to the client and temporary saved by the server.</returns>
        public static StartedAuthentication StartAuthentication(string appId, DeviceRegistration deviceRegistration)
        {
            byte[] challenge = Crypto.GenerateChallenge();
        
            return new StartedAuthentication(
                challenge.ByteArrayToBase64String(),
                appId,
                deviceRegistration.KeyHandle.ByteArrayToBase64String());
        }

        /// <summary>
        /// Finishes a previously started authentication.
        /// </summary>
        /// <param name="startedAuthentication">The authentication the device started</param>
        /// <param name="response">response the response from the token/client.</param>
        /// <param name="deviceRegistration"></param>
        /// <param name="facets">A list of valid facets to verify against. (note: optional)</param>
        /// <returns>the new value of the DeviceRegistration's counter</returns>
        public static uint FinishAuthentication(StartedAuthentication startedAuthentication,
                                                                AuthenticateResponse response,
                                                                DeviceRegistration deviceRegistration,
                                                                HashSet<string> facets = null)
        {
            ClientData clientData = response.GetClientData();
            clientData.CheckContent(AuthenticateTyp, startedAuthentication.Challenge, facets);
        
            RawAuthenticateResponse authenticateResponse = RawAuthenticateResponse.FromBase64(response.SignatureData);
            authenticateResponse.CheckSignature(startedAuthentication.AppId, clientData.AsJson(), deviceRegistration.PublicKey);
            authenticateResponse.CheckUserPresence();
        
            return deviceRegistration.CheckAndUpdateCounter(authenticateResponse.Counter);
        }
    }
}