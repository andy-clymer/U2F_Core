﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using U2F.Core.Models;
using U2F.Demo.DataStore;
using U2F.Demo.Models;
using U2F.Core.Utils;

namespace U2F.Demo.Services
{
    public class MembershipService : IMembershipService
    {
        // NOTE: THIS HAS TO BE UPDATED TO MATCH YOUR SITE/EXAMPLE and sites must be https
        private const string DemoAppId = "https://localhost:44340";
        private readonly U2FContext _dataContext;
        private readonly UserManager<User> _userManager;
        private readonly SignInManager<User> _signInManager;
        private readonly ILogger<MembershipService> _logger;

        public MembershipService(U2FContext userRepository, 
            UserManager<User> userManager, 
            SignInManager<User> signInManager,
            ILogger<MembershipService> logger)
        {
            _dataContext = userRepository;
            _userManager = userManager;
            _signInManager = signInManager;
            _logger = logger;
        }

        #region IMemeberShipService methods

        public async Task<bool> SaveNewUser(string userName, string password, string email)
        {
            bool result = await IsUserRegistered(userName);
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(email)|| result)
                return false;

            User newUser = new User {Name = userName.Trim(), Email = email.Trim(), CreatedOn = DateTime.Now, UserName = userName.Trim()};
            IdentityResult userCreatedResult = await _userManager.CreateAsync(newUser, password.Trim());

            if (userCreatedResult.Succeeded)
            {
                _logger.LogInformation($"user {newUser.Name} was created email {newUser.Email}");
                return true;
            }
            else
            {
                _logger.LogInformation($"failed to create user {userName}");
                foreach (IdentityError error in userCreatedResult.Errors)
                {
                    _logger.LogError(error.Description);
                }
                return false;
            }
        }

        public async Task<ServerRegisterResponse> GenerateServerChallenge(string username)
        {
            User user = await FindUserByUsername(username);
            if (user == null)
                return null;

            StartedRegistration startedRegistration = Core.Crypto.U2F.StartRegistration(DemoAppId);

            if (user.AuthenticationRequest == null)
                user.AuthenticationRequest = new List<AuthenticationRequest>();
            if(user.DeviceRegistrations == null)
                user.DeviceRegistrations = new List<Device>();

            user.AuthenticationRequest.Add(
             new AuthenticationRequest
             {
                 AppId = startedRegistration.AppId,
                 Challenge = startedRegistration.Challenge,
                 Version = Core.Crypto.U2F.U2FVersion
             });

            user.UpdatedOn = DateTime.Now;

            await _dataContext.SaveChangesAsync();

            return new ServerRegisterResponse
            {
                AppId = startedRegistration.AppId,
                Challenge = startedRegistration.Challenge,
                Version = startedRegistration.Version
            };
        }
        
        public async Task<bool> CompleteRegistration(string userName, string deviceResponse)
        {
            if (string.IsNullOrWhiteSpace(deviceResponse))
                return false;

            User user = await FindUserByUsername(userName);

            if (user?.AuthenticationRequest == null || user.AuthenticationRequest.Count == 0)
                return false;


            RegisterResponse registerResponse = RegisterResponse.FromJson<RegisterResponse>(deviceResponse);
            
            // TODO When the user is registration they should only ever have one auth request.????
            AuthenticationRequest authenticationRequest = user.AuthenticationRequest.First();

            StartedRegistration startedRegistration = new StartedRegistration(authenticationRequest.Challenge, authenticationRequest.AppId);
            DeviceRegistration registration = Core.Crypto.U2F.FinishRegistration(startedRegistration, registerResponse);

            user.AuthenticationRequest.Clear();
            user.UpdatedOn = DateTime.Now;            
            user.DeviceRegistrations.Add(new Device
            {
                AttestationCert = registration.AttestationCert,
                Counter = Convert.ToInt32(registration.Counter),
                CreatedOn = DateTime.Now,
                UpdatedOn = DateTime.Now,
                KeyHandle = registration.KeyHandle,
                PublicKey = registration.PublicKey
            });
            int result = await _dataContext.SaveChangesAsync();

            if(result > 0)
                await _signInManager.SignInAsync(user, new AuthenticationProperties(), "U2F");

            return result > 0;
        }

        public async Task<bool> AuthenticateUser(string userName, string deviceResponse)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(deviceResponse))
                return false;

            User user = await FindUserByUsername(userName);
            if (user == null)
                return false;

            AuthenticateResponse authenticateResponse = AuthenticateResponse.FromJson<AuthenticateResponse>(deviceResponse);

            Device device = user.DeviceRegistrations.FirstOrDefault(f => f.KeyHandle.SequenceEqual(authenticateResponse.KeyHandle.Base64StringToByteArray()));

            if (device == null || user.AuthenticationRequest == null)
                return false;

            // User will have a authentication request for each device they have registered so get the one that matches the device key handle
            AuthenticationRequest authenticationRequest = user.AuthenticationRequest.First(f => f.KeyHandle.Equals(authenticateResponse.KeyHandle));
            DeviceRegistration registration = new DeviceRegistration(device.KeyHandle, device.PublicKey, device.AttestationCert, Convert.ToUInt32(device.Counter));

            StartedAuthentication authentication = new StartedAuthentication(authenticationRequest.Challenge, authenticationRequest.AppId, authenticationRequest.KeyHandle);

            Core.Crypto.U2F.FinishAuthentication(authentication, authenticateResponse, registration);
            await _signInManager.SignInAsync(user, new AuthenticationProperties(), "U2F");

            user.AuthenticationRequest.Clear();
            user.UpdatedOn = DateTime.Now;
            
            device.Counter = Convert.ToInt32(registration.Counter);
            device.UpdatedOn = DateTime.Now;

            int result = await _dataContext.SaveChangesAsync();

            return result > 0;
        }

        public async Task<bool> IsUserRegistered(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                return false;

            User user = await FindUserByUsername(userName);

            return user != null;
        }

        public async Task<List<ServerChallenge>> GenerateDeviceChallenges(string userName)
        {
            User user = await FindUserByUsername(userName);

            if (user == null)
                return null;

            // We only want to generate challenges for un-compromised devices
            List<Device> devices = user.DeviceRegistrations.Where(w => w.IsCompromised == false).ToList();

            if (devices.Count == 0)
                return null;

            user.AuthenticationRequest.Clear();
            string challenge = Core.Crypto.U2F.GenerateChallenge();

            List<ServerChallenge> serverChallenges = new List<ServerChallenge>();
            foreach (Device registeredDevice in devices)
            {
                serverChallenges.Add(new ServerChallenge
                {
                    appId = DemoAppId,
                    challenge = challenge,
                    keyHandle = registeredDevice.KeyHandle.ByteArrayToBase64String(),
                    version = Core.Crypto.U2F.U2FVersion
                });
                user.AuthenticationRequest.Add(
                new AuthenticationRequest
                {
                    AppId = DemoAppId,
                    Challenge = challenge,
                    KeyHandle = registeredDevice.KeyHandle.ByteArrayToBase64String(),
                    Version = Core.Crypto.U2F.U2FVersion
                });
            }
            user.UpdatedOn = DateTime.Now;
            await _dataContext.SaveChangesAsync();
            return serverChallenges;
        }

        public async Task<bool> IsValidUserNameAndPassword(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                return false;

            User user = await FindUserByUsername(userName.Trim());
            SignInResult result = await _signInManager.CheckPasswordSignInAsync(user, password.Trim(), false);

            return result.Succeeded;                
        }

        public async Task SignOut()
        {
            await _signInManager.SignOutAsync();
        }

        public async Task<User> FindUserByUsername(string username)
        {
            User user = null;

            if (!string.IsNullOrWhiteSpace(username))
            {
                if (await _dataContext.Users.AnyAsync(person => person.Name == username.Trim()))
                {
                    user = await _dataContext.Users
                        .Include(i => i.AuthenticationRequest)
                        .Include(i => i.DeviceRegistrations)
                        .FirstAsync(person => person.Name == username.Trim());
                }
            }

            return user;
        }

        #endregion
    }
}