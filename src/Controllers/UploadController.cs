﻿// Copyright (c) Cingulara 2019. All rights reserved.
// Licensed under the GNU GENERAL PUBLIC LICENSE Version 3, 29 June 2007 license. See LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;
using System.Xml;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NATS.Client;
using Microsoft.AspNetCore.Authorization;

using openrmf_upload_api.Data;
using openrmf_upload_api.Models;

namespace openrmf_upload_api.Controllers
{
    [Route("/")]
    public class UploadController : Controller
    {
	    private readonly IArtifactRepository _artifactRepo;
	    private readonly ISystemGroupRepository _systemRepo;
      private readonly ILogger<UploadController> _logger;
      private readonly IConnection _msgServer;

        public UploadController(IArtifactRepository artifactRepo, ILogger<UploadController> logger, IOptions<NATSServer> msgServer, ISystemGroupRepository systemRepo )
        {
            _logger = logger;
            _artifactRepo = artifactRepo;
            _systemRepo = systemRepo;
            _msgServer = msgServer.Value.connection;
        }

        // POST as new
        [HttpPost]
        [Authorize(Roles = "Administrator,Editor,Assessor")]
        public async Task<IActionResult> UploadNewChecklist(List<IFormFile> checklistFiles, string systemGroupId, string system="None")
        {
          try {
            if (checklistFiles.Count > 0) {

              // grab the user/system ID from the token if there which is *should* always be
              var claim = this.User.Claims.Where(x => x.Type == System.Security.Claims.ClaimTypes.NameIdentifier).FirstOrDefault();
              // make sure the SYSTEM GROUP is valid here and then add the files...
              SystemGroup sg;
              SystemGroup recordSystem = null;

              if (string.IsNullOrEmpty(systemGroupId)) {
                sg = new SystemGroup();
                sg.title = system;
                sg.created = DateTime.Now;
                if (claim != null && claim.Value != null) {
                  sg.createdBy = Guid.Parse(claim.Value);
                }
                recordSystem = _systemRepo.AddSystemGroup(sg).GetAwaiter().GetResult();
              } else {
                sg = await _systemRepo.GetSystemGroup(systemGroupId);
                if (sg == null) {
                  sg = new SystemGroup();
                  sg.title = "None";
                  sg.created = DateTime.Now;
                  if (claim != null && claim.Value != null) {
                    sg.createdBy = Guid.Parse(claim.Value);
                  }
                recordSystem = _systemRepo.AddSystemGroup(sg).GetAwaiter().GetResult();
                }
                else {
                  sg.updatedOn = DateTime.Now;
                  if (claim != null && claim.Value != null) {
                    sg.updatedBy = Guid.Parse(claim.Value);
                  }
                  var updated = _systemRepo.UpdateSystemGroup(systemGroupId, sg).GetAwaiter().GetResult();
                }
              }

              // now go through the Checklists and set them up
              foreach(IFormFile file in checklistFiles) {
                string rawChecklist =  string.Empty;

                if (file.FileName.ToLower().EndsWith(".xml")) {
                  // if an XML XCCDF SCAP scan file
                  using (var reader = new StreamReader(file.OpenReadStream()))
                  {
                    // read in the file
                    string xmlfile = reader.ReadToEnd();
                    // pull out the rule IDs and their results of pass or fail and the title/type of SCAP scan done
                    SCAPRuleResultSet results = SCAPScanResultLoader.LoadSCAPScan(xmlfile);
                    // get the rawChecklist data so we can move on
                    // generate a new checklist from a template based on the type and revision
                    rawChecklist = SCAPScanResultLoader.GenerateChecklistData(results);
                  }
                }
                else if (file.FileName.ToLower().EndsWith(".ckl")) {
                  // if a CKL file
                  using (var reader = new StreamReader(file.OpenReadStream()))
                  {
                      rawChecklist = reader.ReadToEnd();  
                  }
                }
                else {
                  // log this is a bad file
                  return BadRequest();
                }

                // clean up any odd data that can mess us up moving around, via JS, and such
                rawChecklist = SanitizeData(rawChecklist);

                // create the new record for saving into the DB
                Artifact newArtifact = MakeArtifactRecord(rawChecklist);

                if (claim != null) { // get the value
                  newArtifact.createdBy = Guid.Parse(claim.Value);
                  if (sg.createdBy == Guid.Empty)
                    sg.createdBy = Guid.Parse(claim.Value);
                  else 
                    sg.updatedBy = Guid.Parse(claim.Value);
                }

                // add the system record ID to the Artifact to know how to query it
                if (recordSystem != null) {
                  newArtifact.systemGroupId = recordSystem.InternalId.ToString();
                  // store the title for ease of use
                  newArtifact.systemTitle = recordSystem.title;
                }
                else {
                  newArtifact.systemGroupId = sg.InternalId.ToString();
                  // store the title for ease of use
                  newArtifact.systemTitle = sg.title;
                }
                // save the artifact record and checklist to the database
                var record = await _artifactRepo.AddArtifact(newArtifact);

                // publish to the openrmf save new realm the new ID we can use
                _msgServer.Publish("openrmf.checklist.save.new", Encoding.UTF8.GetBytes(record.InternalId.ToString()));
                // publish to update the system checklist count
                _msgServer.Publish("openrmf.system.count.add", Encoding.UTF8.GetBytes(record.systemGroupId));
                _msgServer.Flush();
              }
              return Ok();
            }
            else
                return BadRequest();
          }
          catch (Exception ex) {
              _logger.LogError(ex, "Error uploading checklist file");
              return BadRequest();
          }
        }

        // PUT as update
        [HttpPut("{id}")]
        [Authorize(Roles = "Administrator,Editor,Assessor")]
        public async Task<IActionResult> UpdateChecklist(string id, IFormFile checklistFile, string systemGroupId)
        {
          try {
              var name = checklistFile.FileName;
              string rawChecklist =  string.Empty;
              if (checklistFile.FileName.ToLower().EndsWith(".xml")) {
                // if an XML XCCDF SCAP scan checklistFile
                using (var reader = new StreamReader(checklistFile.OpenReadStream()))
                {
                  // read in the checklistFile
                  string xmlfile = reader.ReadToEnd();
                  // pull out the rule IDs and their results of pass or fail and the title/type of SCAP scan done
                  SCAPRuleResultSet results = SCAPScanResultLoader.LoadSCAPScan(xmlfile);
                  // get the raw checklist from the msg checklist NATS reader                  
                  // update the rawChecklist data so we can move on
                  var record = await _artifactRepo.GetArtifact(id);
                  rawChecklist = SCAPScanResultLoader.UpdateChecklistData(results, record.rawChecklist, false);
                }
              }
              else if (checklistFile.FileName.ToLower().EndsWith(".ckl")) {
                // if a CKL file
                using (var reader = new StreamReader(checklistFile.OpenReadStream()))
                {
                    rawChecklist = reader.ReadToEnd();  
                }
              }
              else {
                // log this is a bad checklistFile
                return BadRequest();
              }

              rawChecklist = SanitizeData(rawChecklist);
              // update and fill in the same info
              Artifact newArtifact = MakeArtifactRecord(rawChecklist);
              Artifact oldArtifact = await _artifactRepo.GetArtifact(id);
              if (oldArtifact != null && oldArtifact.createdBy != Guid.Empty){
                // this is an update of an older one, keep the createdBy intact
                newArtifact.createdBy = oldArtifact.createdBy;
                // keep it a part of the same system group
                if (!string.IsNullOrEmpty(oldArtifact.systemGroupId)) {
                  newArtifact.systemGroupId = oldArtifact.systemGroupId;
                  newArtifact.systemTitle = oldArtifact.systemTitle;
                }
              }
              oldArtifact = null;

              // grab the user/system ID from the token if there which is *should* always be
              var claim = this.User.Claims.Where(x => x.Type == System.Security.Claims.ClaimTypes.NameIdentifier).FirstOrDefault();
              if (claim != null) { // get the value
                newArtifact.updatedBy = Guid.Parse(claim.Value);
              }

              await _artifactRepo.UpdateArtifact(id, newArtifact);
              // publish to the openrmf save new realm the new ID we can use
              _msgServer.Publish("openrmf.checklist.save.update", Encoding.UTF8.GetBytes(id));
              _msgServer.Flush();
              return Ok();
          }
          catch (Exception ex) {
              _logger.LogError(ex, "Error Uploading updated Checklist file");
              return BadRequest();
          }
      }
      
      // this parses the text and system, generates the pieces, and returns the artifact to save
      private Artifact MakeArtifactRecord(string rawChecklist) {
        Artifact newArtifact = new Artifact();
        newArtifact.created = DateTime.Now;
        newArtifact.updatedOn = DateTime.Now;
        newArtifact.rawChecklist = rawChecklist;

        // parse the checklist and get the data needed
        rawChecklist = rawChecklist.Replace("\n","").Replace("\t","");
        XmlDocument xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(rawChecklist);

        newArtifact.hostName = "Unknown-Host";
        XmlNodeList assetList = xmlDoc.GetElementsByTagName("ASSET");
        // get the host name from here
        foreach (XmlElement child in assetList.Item(0).ChildNodes)
        {
          switch (child.Name) {
            case "HOST_NAME":
              if (!string.IsNullOrEmpty(child.InnerText)) 
                newArtifact.hostName = child.InnerText;
              break;
          }
        }
        // get the title and release which is a list of children of child nodes buried deeper :face-palm-emoji:
        XmlNodeList stiginfoList = xmlDoc.GetElementsByTagName("STIG_INFO");
        foreach (XmlElement child in stiginfoList.Item(0).ChildNodes) {
          if (child.FirstChild.InnerText == "releaseinfo")
            newArtifact.stigRelease = child.LastChild.InnerText;
          else if (child.FirstChild.InnerText == "title")
            newArtifact.stigType = child.LastChild.InnerText;
        }

        // shorten the names a bit
        if (newArtifact != null && !string.IsNullOrEmpty(newArtifact.stigType)){
          newArtifact.stigType = newArtifact.stigType.Replace("Security Technical Implementation Guide", "STIG");
          newArtifact.stigType = newArtifact.stigType.Replace("Windows", "WIN");
          newArtifact.stigType = newArtifact.stigType.Replace("Application Security and Development", "ASD");
          newArtifact.stigType = newArtifact.stigType.Replace("Microsoft Internet Explorer", "MSIE");
          newArtifact.stigType = newArtifact.stigType.Replace("Red Hat Enterprise Linux", "REL");
          newArtifact.stigType = newArtifact.stigType.Replace("MS SQL Server", "MSSQL");
          newArtifact.stigType = newArtifact.stigType.Replace("Server", "SVR");
          newArtifact.stigType = newArtifact.stigType.Replace("Workstation", "WRK");
        }
        if (newArtifact != null && !string.IsNullOrEmpty(newArtifact.stigRelease)) {
          newArtifact.stigRelease = newArtifact.stigRelease.Replace("Release: ", "R"); // i.e. R11, R2 for the release number
          newArtifact.stigRelease = newArtifact.stigRelease.Replace("Benchmark Date:","dated");
        }
        return newArtifact;
      }

      private string SanitizeData (string rawdata) {
        return rawdata.Replace("\t","").Replace(">\n<","><");
      }
    }
}
